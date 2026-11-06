using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DevicePower.Devices;

namespace DevicePower.Taskbar;

/// <summary>
/// 把电量条浮在任务栏上（Win11 的任务栏内容由组合层渲染，普通子窗口会被盖住，
/// 这里与 TrafficMonitor 在 Win11 下的做法一致：顶层透明窗口 + 每秒跟随任务栏）。
/// </summary>
internal sealed class TaskbarDock : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectReorder = 0x8004;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly DeviceMonitor _monitor;
    private readonly TaskbarStrip _strip;
    private readonly DispatcherTimer _timer;
    private readonly WinEventDelegate _zOrderProc;
    private readonly WinEventDelegate _foregroundProc;
    private readonly AutoResetEvent _guardSignal = new(false);
    private Thread _guardThread;
    private IntPtr _hwnd;
    private IntPtr _zOrderHook;
    private IntPtr _foregroundHook;
    private long _guardHwnd;
    private long _guardDeadline;
    private volatile bool _guardStop;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private const double GlideMs = 180;
    private const double GlideSkipPx = 80;
    private double _glideFromLeft;
    private double _glideFromTop;
    private long _glideStartTick;
    private bool _gliding;
    private double _currentLeft = double.NaN;
    private double _currentTop = double.NaN;
    private int _targetLeft;
    private int _targetTop;
    private bool _hasTarget;
    private int _hideStreak;
    private string _lastLog = "";
    private bool _disposed;

    public TaskbarDock(DeviceMonitor monitor)
    {
        _monitor = monitor;
        _strip = new TaskbarStrip(monitor);
        _strip.RefreshRequested += monitor.RequestRefresh;
        _zOrderProc = OnZOrderChanged;
        _foregroundProc = OnForegroundChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
    }

    public void Show()
    {
        if (_disposed) return;

        if (!_strip.IsVisible) _strip.Show();
        _hwnd = new WindowInteropHelper(_strip).Handle;
        Interlocked.Exchange(ref _guardHwnd, _hwnd.ToInt64());

        Update();
        if (!_timer.IsEnabled) _timer.Start();
        StartGuard();

        if (_zOrderHook == IntPtr.Zero)
            _zOrderHook = SetWinEventHook(EventObjectReorder, EventObjectReorder, IntPtr.Zero,
                _zOrderProc, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _foregroundProc, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
    }

    public void Hide()
    {
        _timer.Stop();
        StopGlide();
        Interlocked.Exchange(ref _guardHwnd, 0);
        UnhookZOrder();
        _strip.Hide();
        _lastX = int.MinValue;
        _currentLeft = double.NaN;
        _hasTarget = false;
    }

    /// <summary>
    /// 任务栏被点成前台窗口时会把自己抬到电量条上面约 30ms。
    /// 事件只负责唤醒守护线程，真正的置顶在守护线程里 1ms 轮询，缩到不足一帧。
    /// </summary>
    private void OnZOrderChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;
        PulseGuard(120);
    }

    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;

        // 焦点给谁都重新压一次层级：桌面（没有窗口）被激活时，任务栏会盖到电量条上面
        PulseGuard(220);
    }

    private void StartGuard()
    {
        if (_guardThread != null) return;

        _guardThread = new Thread(GuardLoop) { IsBackground = true, Name = "TaskbarZOrderGuard" };
        _guardThread.Start();
    }

    private void PulseGuard(int milliseconds)
    {
        long until = Environment.TickCount64 + milliseconds;
        long current;
        do
        {
            current = Volatile.Read(ref _guardDeadline);
            if (current >= until) break;
        }
        while (Interlocked.CompareExchange(ref _guardDeadline, until, current) != current);

        _guardSignal.Set();
    }

    private void GuardLoop()
    {
        while (!_guardStop)
        {
            bool pulsed = _guardSignal.WaitOne(1000);
            if (!pulsed)
            {
                ReassertTopmost();
                continue;
            }

            // 重新置顶现在是无条件 SetWindowPos，粒度放到 4ms 已经远小于一帧，又不至于刷太密
            timeBeginPeriod(1);
            try
            {
                while (!_guardStop && Environment.TickCount64 < Volatile.Read(ref _guardDeadline))
                {
                    ReassertTopmost();
                    Thread.Sleep(4);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }

            ReassertTopmost();
        }
    }

    private void ReassertTopmost()
    {
        IntPtr hwnd = new(Interlocked.Read(ref _guardHwnd));
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return;

        // 这里不能用 IsAbove 判断：一旦我们自己掉出置顶带，从自己往上遍历根本走不到任务栏，
        // 会被误判成"我们在上面"，之后就再也不重新置顶了
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void UnhookZOrder()
    {
        if (_zOrderHook != IntPtr.Zero)
        {
            UnhookWinEvent(_zOrderHook);
            _zOrderHook = IntPtr.Zero;
        }

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    private void Update()
    {
        if (_disposed) return;
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(_strip).Handle;
        if (_hwnd == IntPtr.Zero) return;

        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            if (_strip.IsVisible) _strip.Hide();
            return;
        }

        RECT tb;
        GetWindowRect(taskbar, out tb);
        MONITORINFO monitor = MonitorInfo(taskbar);

        string reason = HideReason(tb, monitor, out int state);

        // 连续两次判定要隐藏才真隐藏，单次抖动不该把面板关掉一下
        _hideStreak = reason == null ? 0 : _hideStreak + 1;
        bool hide = _hideStreak >= 2;

        Log("tb=" + tb.Left + "," + tb.Top + "," + (tb.Right - tb.Left) + "x" + (tb.Bottom - tb.Top) +
            " screen=" + monitor.rcMonitor.Left + "," + monitor.rcMonitor.Top + "," +
            (monitor.rcMonitor.Right - monitor.rcMonitor.Left) + "x" + (monitor.rcMonitor.Bottom - monitor.rcMonitor.Top) +
            " quns=" + state + " visible=" + (hide ? 0 : 1) + " reason=" + (reason ?? "-"));

        if (hide)
        {
            if (_strip.IsVisible) _strip.Hide();
            return;
        }

        if (!_strip.IsVisible) _strip.Show();
        else SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        double scale = GetDpiForWindow(taskbar) / 96.0;
        _monitor.Look.SetStripLimit((tb.Bottom - tb.Top) / scale, _monitor.Rows.Count >= 2 ? 2 : 1);

        RECT self;
        // 列宽/字号变化要等布局跑完才反映到窗口尺寸上，不然这里量到的是旧尺寸，
        // 新位置得等下一秒的定时器才生效
        _strip.UpdateLayout();
        GetWindowRect(_hwnd, out self);
        int width = self.Right - self.Left;
        int height = self.Bottom - self.Top;
        if (width <= 0 || height <= 0) return;

        int margin = Math.Max(2, (int)Math.Round(6 * scale));

        int left;
        int top;

        if (tb.Right - tb.Left >= tb.Bottom - tb.Top)
        {
            top = tb.Top + (tb.Bottom - tb.Top - height) / 2;

            if (_monitor.Settings.TaskbarSide == "left")
            {
                left = tb.Left + margin;
            }
            else
            {
                int trayLeft = TrayLeft(taskbar, tb, scale);
                left = trayLeft - width - Math.Max(4, (int)Math.Round(10 * scale));

                // 任务按钮区已经顶到窗口位置时，改放到开始按钮左侧
                if (TaskListRight(taskbar) > left - margin)
                    left = LeftSlot(taskbar, tb, width, margin);
            }
        }
        else
        {
            left = tb.Left + (tb.Right - tb.Left - width) / 2;
            top = TrayTop(taskbar, tb, scale) - height - Math.Max(4, (int)Math.Round(10 * scale));
            if (top < tb.Top + margin) top = tb.Bottom - height - margin;
        }

        if (left < tb.Left + margin) left = tb.Left + margin;
        if (left + width > tb.Right - margin) left = tb.Right - width - margin;
        if (top < tb.Top) top = tb.Top;
        if (top + height > tb.Bottom) top = tb.Bottom - height;

        if (left == _targetLeft && top == _targetTop && _hasTarget)
        {
            // 位置没变：缓动由渲染帧自己推进，这里不再多踩一步（之前每秒 tick 叠加一步就是卡顿的来源）
            return;
        }

        _targetLeft = left;
        _targetTop = top;
        _hasTarget = true;

        if (double.IsNaN(_currentLeft))
        {
            FinishGlide(left, top);
            return;
        }

        StartGlide(left, top);
    }

    /// <summary>
    /// 位置缓动：固定 180ms 的三次缓入缓出，由渲染帧驱动（DispatcherTimer 不跟 vsync，
    /// 帧距会飘，看着就是一顿一顿）。距离太短直接到位，太长慢爬反而怪，超过阈值直接跳。
    /// </summary>
    private void StartGlide(int left, int top)
    {
        double distance = Math.Max(Math.Abs(left - _currentLeft), Math.Abs(top - _currentTop));

        if (distance <= 2 || distance > GlideSkipPx)
        {
            FinishGlide(left, top);
            return;
        }

        _glideFromLeft = _currentLeft;
        _glideFromTop = _currentTop;
        _glideStartTick = Environment.TickCount64;
        _targetLeft = left;
        _targetTop = top;

        if (_gliding) return;
        _gliding = true;
        System.Windows.Media.CompositionTarget.Rendering += OnRenderFrame;
    }

    private void OnRenderFrame(object sender, EventArgs e)
    {
        double t = (Environment.TickCount64 - _glideStartTick) / GlideMs;

        if (t >= 1.0)
        {
            FinishGlide(_targetLeft, _targetTop);
            return;
        }

        double k = EaseInOut(Math.Max(t, 0));
        Place(
            (int)Math.Round(_glideFromLeft + (_targetLeft - _glideFromLeft) * k),
            (int)Math.Round(_glideFromTop + (_targetTop - _glideFromTop) * k));
    }

    private void FinishGlide(int left, int top)
    {
        _currentLeft = left;
        _currentTop = top;
        StopGlide();
        Place(left, top);
    }

    private void StopGlide()
    {
        if (!_gliding) return;
        _gliding = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnRenderFrame;
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private void Place(int left, int top)
    {
        if (left == _lastX && top == _lastY) return;
        _lastX = left;
        _lastY = top;
        SetWindowPos(_hwnd, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate);
    }

    /// <summary>返回 null 表示该显示；否则给出隐藏原因，方便在家用电脑上查“面板不见了”到底是谁干的。</summary>
    private static string HideReason(RECT tb, MONITORINFO monitor, out int state)
    {
        state = 0;
        RECT screen = monitor.rcMonitor;

        if (tb.Top >= screen.Bottom - 2) return "taskbar-off-bottom";
        if (tb.Bottom <= screen.Top + 2) return "taskbar-off-top";
        if (tb.Left >= screen.Right - 2) return "taskbar-off-right";
        if (tb.Right <= screen.Left + 2) return "taskbar-off-left";

        if (SHQueryUserNotificationState(out state) == 0 &&
            (state == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ || state == 4 /* QUNS_PRESENTATION_MODE */))
            return "notification-state=" + state;

        // QUNS_BUSY(2) 不能用来判断：家用机器上它在桌面空闲时也会反复跳到 2，
        // 面板就会每两秒被自己隐藏一次（2026-11-06 家里电脑日志实测 2↔5 抖动）
        return null;
    }

    private void Log(string line)
    {
        if (line == _lastLog) return;
        _lastLog = line;

        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevicePower");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "dock.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 200000)
                System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static int TrayLeft(IntPtr taskbar, RECT tb, double scale)
    {
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero)
        {
            RECT rect;
            if (GetWindowRect(tray, out rect) && rect.Right > rect.Left) return rect.Left;
        }

        return tb.Right - (int)Math.Round(180 * scale);
    }

    private static int TrayTop(IntPtr taskbar, RECT tb, double scale)
    {
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero)
        {
            RECT rect;
            if (GetWindowRect(tray, out rect) && rect.Bottom > rect.Top) return rect.Top;
        }

        return tb.Bottom - (int)Math.Round(180 * scale);
    }

    private static int TaskListRight(IntPtr taskbar)
    {
        IntPtr rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar != IntPtr.Zero)
        {
            IntPtr list = FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
            if (list == IntPtr.Zero) list = FindWindowEx(rebar, IntPtr.Zero, "MSTaskListWClass", null);

            RECT rect;
            if (list != IntPtr.Zero && GetWindowRect(list, out rect) && rect.Right > rect.Left)
                return rect.Right;

            if (GetWindowRect(rebar, out rect) && rect.Right > rect.Left)
                return rect.Right;
        }

        IntPtr fallback = FindWindowEx(taskbar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (fallback != IntPtr.Zero && GetWindowRect(fallback, out RECT listRect))
            return listRect.Right;

        return int.MinValue;
    }

    private static int LeftSlot(IntPtr taskbar, RECT tb, int width, int margin)
    {
        IntPtr start = FindWindowEx(taskbar, IntPtr.Zero, "Start", null);
        if (start != IntPtr.Zero)
        {
            RECT rect;
            if (GetWindowRect(start, out rect) && rect.Right > rect.Left)
                return Math.Max(tb.Left + margin, rect.Left - width - margin);
        }

        return tb.Left + margin;
    }

    private static MONITORINFO MonitorInfo(IntPtr window)
    {
        IntPtr monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor != IntPtr.Zero) GetMonitorInfo(monitor, ref info);
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        StopGlide();
        UnhookZOrder();

        _guardStop = true;
        _guardSignal.Set();
        _guardThread?.Join(500);
        _guardSignal.Dispose();

        _strip.Close();
    }
}
