using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DevicePower.Devices;

namespace DevicePower;

internal sealed class TrayHost : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const uint NimModify = 0x1;
    private const uint NifInfo = 0x10;
    private const uint NiifUser = 0x4;
    private const uint NiifLargeIcon = 0x20;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private readonly NotifyIcon _icon;
    private readonly DeviceMonitor _monitor;
    private readonly BatteryPanel _panel;
    private readonly Icon _notifyIcon;
    private TrayMenuWindow _menu;
    private long _menuClosedTick;
    private SettingsWindow _settings;
    private AboutWindow _about;
    private Icon _currentIcon;
    private IntPtr _trayHwnd;
    private uint _trayId;
    private Bitmap _logo;
    private string _logoPath;
    private DateTime _logoStamp;
    private readonly Dictionary<string, int> _warned = new();
    private int _iconLevel = int.MinValue;

    public TrayHost(DeviceMonitor monitor, BatteryPanel panel)
    {
        _monitor = monitor;
        _panel = panel;

        _currentIcon = CreateTrayIcon(-1);
        _notifyIcon = LoadNotifyIcon();
        _icon = new NotifyIcon
        {
            Visible = true,
            Icon = _currentIcon,
            Text = "外设电量",
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMenu();
        };

        monitor.Updated += OnUpdated;
        monitor.DeviceOnline += OnDeviceOnline;
    }

    internal void ShowMenu()
    {
        // 点击任务栏电量条会让菜单先因失焦关闭，紧接着的这一次右键不该再把它弹出来
        if (Environment.TickCount64 - _menuClosedTick < 400) return;

        // 菜单已经打开时再次右键（托盘或电量条）就关掉，行为像开关
        if (_menu != null && _menu.IsVisible)
        {
            _menu.Close();
            return;
        }

        var menu = new TrayMenuWindow
        {
            Left = -20000,
            Top = -20000,
        };
        menu.SettingsRequested += OpenSettings;
        menu.TogglePanelRequested += TogglePanel;
        menu.RefreshRequested += _monitor.RequestRefresh;
        menu.AboutRequested += ShowAbout;
        menu.ExitRequested += () => System.Windows.Application.Current?.Shutdown();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
            _menuClosedTick = Environment.TickCount64;
        };
        _menu = menu;
        menu.Show();
    }

    private void TogglePanel()
    {
        if (_panel.IsVisible) _panel.Hide();
        else _panel.ShowNearTray();
    }

    private void OpenSettings()
    {
        // 右键菜单在设置窗口的嵌套消息循环里仍然能弹出，这里保证只留一个设置窗口
        if (_settings != null)
        {
            if (_settings.WindowState == System.Windows.WindowState.Minimized)
                _settings.WindowState = System.Windows.WindowState.Normal;
            _settings.Activate();
            return;
        }

        var window = new SettingsWindow(_monitor);
        _settings = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settings, window)) _settings = null;
        };
        window.ShowDialog();
    }

    private void ShowAbout()
    {
        if (_about != null)
        {
            if (!_about.IsVisible) _about.Show();
            _about.Activate();
            return;
        }

        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }

    private void OnUpdated()
    {
        List<DeviceRow> rows = _monitor.Rows.ToList();

        string tip = rows.Count == 0
            ? "外设电量"
            : string.Join("  |  ", rows.Select(r =>
                $"{r.Name} {r.PercentText}{(r.IsAsleep ? " 休眠" : r.IsCharging ? " 充电中" : "")}"));
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        int level = rows.Where(r => r.Level >= 0).Select(r => r.Level).DefaultIfEmpty(-1).Min();
        if (level != _iconLevel)
        {
            _iconLevel = level;
            ApplyTrayIcon(level);
        }

        CheckWarnings(rows);
    }

    private void ApplyTrayIcon(int level)
    {
        Icon icon = CreateTrayIcon(level);
        _icon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private void CheckWarnings(List<DeviceRow> rows)
    {
        int low = _monitor.Settings.LowThreshold;
        int critical = _monitor.Settings.CriticalThreshold;

        foreach (DeviceRow row in rows)
        {
            if (row.Level < 0) continue;

            // 充电中不要提示“请充电”
            if (row.IsCharging)
            {
                _warned[row.Key] = 0;
                continue;
            }

            int severity = row.Level <= critical ? 2 : row.Level <= low ? 1 : 0;
            _warned.TryGetValue(row.Key, out int previous);

            if (severity > previous && severity == 2)
                Notify("电量极低", $"{row.Name} 剩余 {row.Level}%，请立即充电");
            else if (severity > previous)
                Notify("电量偏低", $"{row.Name} 剩余 {row.Level}%，请及时充电");

            _warned[row.Key] = severity;
        }
    }

    private void OnDeviceOnline(DeviceRow row)
    {
        bool on = _monitor.Settings.NotifyOnline;
        DiagLog.WriteIfChanged("online", $"{row.Key} level={row.Level} notify={(on ? 1 : 0)}");
        if (!on) return;
        Notify("设备已上线", $"{row.Name} 已连接，当前电量 {row.PercentText}", ToolTipIcon.Info);
    }

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        // 用 Shell_NotifyIcon 的 NIIF_USER + hBalloonIcon 指定通知图片（hlogo），
        // NotifyIcon.ShowBalloonTip 不支持自定义 balloon 图标，只能退回系统默认图标
        if (TryShowBalloon(title, text)) return;

        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    private bool TryShowBalloon(string title, string text)
    {
        if (_notifyIcon == null || !ResolveTrayTarget()) return false;

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _trayHwnd,
            uID = _trayId,
            uFlags = NifInfo,
            hIcon = IntPtr.Zero,
            szTip = "",
            szInfo = Truncate(text, 255),
            uTimeoutOrVersion = 5000,
            szInfoTitle = Truncate(title, 63),
            dwInfoFlags = NiifUser | NiifLargeIcon,
            hBalloonIcon = _notifyIcon.Handle,
        };

        return Shell_NotifyIcon(NimModify, ref data);
    }

    private bool ResolveTrayTarget()
    {
        if (_trayHwnd != IntPtr.Zero) return true;
        if (_icon == null) return false;

        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo windowField = typeof(NotifyIcon).GetField("_window", Flags);
        FieldInfo idField = typeof(NotifyIcon).GetField("_id", Flags);
        if (windowField?.GetValue(_icon) is not NativeWindow window || idField == null) return false;

        _trayHwnd = window.Handle;
        _trayId = (uint)idField.GetValue(_icon);
        return _trayHwnd != IntPtr.Zero;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max];

    private static Icon LoadNotifyIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/hlogo.ico"));
            if (resource?.Stream == null) return null;

            using (Stream stream = resource.Stream) return new Icon(stream, new Size(32, 32));
        }
        catch
        {
            return null;
        }
    }

    private Icon CreateTrayIcon(int level)
    {
        Bitmap logo = GetLogo();
        if (logo == null) return DrawBatteryIcon(level);

        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 图标本来就是托盘尺寸时原样绘制，避免缩放糊掉
            if (logo.Width == size && logo.Height == size)
            {
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.DrawImageUnscaled(logo, 0, 0);
            }
            else if (logo.Width < size)
            {
                // 小图标（如 16px）放大到高 DPI 托盘尺寸时用最近邻，保持像素边缘清晰
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(logo, new Rectangle(0, 0, size, size));
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(logo, new Rectangle(0, 0, size, size));
            }

            if (level >= 0 && level <= _monitor.Settings.LowThreshold)
            {
                Color fill = level <= _monitor.Settings.CriticalThreshold
                    ? Color.FromArgb(0xFF, 0x6B, 0x6B)
                    : Color.FromArgb(0xFF, 0xC8, 0x57);

                float dot = Math.Max(4f, size / 3f);
                float x = size - dot - 0.5f;
                float y = size - dot - 0.5f;

                using var brush = new SolidBrush(fill);
                using var pen = new Pen(Color.FromArgb(0xB0, 0, 0, 0), 1f);
                g.FillEllipse(brush, x, y, dot, dot);
                g.DrawEllipse(pen, x, y, dot, dot);
            }
        }

        IntPtr handle = canvas.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>优先用设置里选的 .ico，其次用随程序附带的 Assets\tray.ico。</summary>
    private Bitmap GetLogo()
    {
        string path = _monitor.Settings.TrayIconPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (!File.Exists(path)) return null;

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (_logo != null && _logoPath == path && _logoStamp == stamp) return _logo;

        _logo?.Dispose();
        _logo = null;
        try
        {
            _logo = new Bitmap(path);
            _logoPath = path;
            _logoStamp = stamp;
        }
        catch
        {
            _logoPath = null;
        }

        return _logo;
    }

    private static Icon DrawBatteryIcon(int level)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(Color.White, 2f);
            g.DrawRectangle(pen, 5, 9, 18, 14);
            g.FillRectangle(Brushes.White, 24, 14, 3, 5);

            if (level >= 0)
            {
                int width = (int)Math.Round(14.0 * Math.Clamp(level, 0, 100) / 100.0);
                if (width > 0) g.FillRectangle(Brushes.White, 7, 11, width, 10);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _monitor.Updated -= OnUpdated;
        _monitor.DeviceOnline -= OnDeviceOnline;
        _menu?.Close();
        _settings?.Close();
        _about?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _notifyIcon?.Dispose();
        _currentIcon?.Dispose();
        _logo?.Dispose();
    }
}
