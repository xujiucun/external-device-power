using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DevicePower.Taskbar;

/// <summary>
/// 前景窗口有没有铺满它自己那块屏。QUNS 只在 DX 独占全屏时报 3，英雄联盟这类
/// "无边框全屏"始终停在 5，所以必须直接量前台窗口的矩形。
/// </summary>
internal static class FullscreenWatcher
{
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

    /// <summary>桌面、任务栏、外壳浮层本来就占满整块屏，不能算全屏应用。</summary>
    private static readonly string[] ShellClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Taskband_OverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "WindowsDashboard",
    };

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;

    /// <summary>
    /// 会开一个铺满整屏的普通窗口的截图进程。微信选区画布的窗口类是 Qt…QWindowIcon，
    /// 和不带边框的游戏窗口在几何上完全一样，只能按进程名分。
    /// </summary>
    private static readonly string[] CaptureExes =
    {
        "Weixin.exe",
        "WeChat.exe",
        "QQ.exe",
        "Snipaste.exe",
        "PixPin.exe",
    };

    /// <summary>真正的全屏应用（游戏）；null = 没有。</summary>
    public static string CoveringReason(IntPtr self) => Probe(self, false);

    /// <summary>
    /// 开始菜单/搜索是否正在显示。Win11 打开开始菜单时任务栏会被提到更高的窗口层，
    /// 普通置顶窗口会被它盖住；这里用于主动隐藏，避免“状态显示可见、实际被盖住”。
    /// 关闭后 SearchHost 窗口可能仍是前台且 IsWindowVisible=true，但会被 DWM cloak，
    /// 所以必须同时检查 cloak，否则会一直隐藏到用户点击别处。
    /// </summary>
    public static bool IsStartMenuOpen()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        // 关闭开始菜单后，SearchHost 的窗口仍然存在且 IsWindowVisible 为 true，
        // 但会被 DWM cloak（cloak=2）；必须把这种情况排除，否则会一直保持隐藏。
        if (!IsWindowVisible(foreground)) return false;
        if (DwmGetWindowAttribute(foreground, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0)
            return false;

        if (!string.Equals(ClassName(foreground), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            return false;

        string exe = ExeName(foreground);
        return string.Equals(exe, "SearchHost.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exe, "StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exe, "ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 截图/覆盖层：铺满屏幕的工具窗口（系统截图、Snipaste）或已知截图进程（微信画布）。
    /// 这段时间任务栏托盘区会临时变窄，电量条跟着重算就会"自己往左跑"，所以要冻结位置。
    /// </summary>
    public static string OverlayReason(IntPtr self) => Probe(self, true);

    private static string Probe(IntPtr self, bool wantOverlay)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == self) return null;

        string className = ClassName(foreground);
        foreach (string shell in ShellClasses)
        {
            if (string.Equals(shell, className, StringComparison.Ordinal)) return null;
        }

        string exe = ExeName(foreground);
        bool overlay = (GetWindowLong(foreground, GwlExStyle) & WsExToolwindow) != 0 || IsCapture(exe);
        if (overlay != wantOverlay) return null;

        if (!GetWindowRect(foreground, out RECT window)) return null;
        MONITORINFO on = Info(MonitorFromWindow(foreground, 2 /* TONEAREST */));
        if (!Covers(window, on.rcMonitor)) return null;

        if (self != IntPtr.Zero)
        {
            // 副屏全屏不该把主屏的电量 UI 关掉
            MONITORINFO ours = Info(MonitorFromWindow(self, 2));
            if (!SameMonitor(on.rcMonitor, ours.rcMonitor)) return null;
        }

        return className + " " + (window.Right - window.Left) + "x" + (window.Bottom - window.Top) + " " + exe;
    }

    private static bool IsCapture(string exe)
    {
        foreach (string name in CaptureExes)
        {
            if (string.Equals(name, exe, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>窗口属于哪个进程：微信截图画布和游戏窗口几何上一样，只能靠这个区分。</summary>
    private static string ExeName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "pid=?";

        IntPtr process = OpenProcess(0x1000 /* QUERY_LIMITED */, false, pid);
        if (process == IntPtr.Zero) return "pid=" + pid;

        try
        {
            var buffer = new StringBuilder(512);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size)
                ? System.IO.Path.GetFileName(buffer.ToString(0, size))
                : "pid=" + pid;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool Covers(RECT window, RECT monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    private static bool SameMonitor(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? "" : buffer.ToString(0, length);
    }

    private static MONITORINFO Info(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor != IntPtr.Zero) GetMonitorInfo(monitor, ref info);
        return info;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
