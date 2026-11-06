using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DevicePower.Devices;

namespace DevicePower;

internal sealed class TrayHost : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _icon;
    private readonly DeviceMonitor _monitor;
    private readonly BatteryPanel _panel;
    private TrayMenuWindow _menu;
    private AboutWindow _about;
    private Icon _currentIcon;
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
    }

    private void ShowMenu()
    {
        if (_menu != null && _menu.IsVisible) return;

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
        var window = new SettingsWindow(_monitor);
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
                $"{r.Name} {r.PercentText}{(r.IsCharging ? " 充电中" : "")}"));
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        int level = rows.Where(r => r.Level >= 0).Select(r => r.Level).DefaultIfEmpty(-1).Min();
        if (level != _iconLevel)
        {
            _iconLevel = level;
            Icon icon = CreateTrayIcon(level);
            _icon.Icon = icon;
            _currentIcon?.Dispose();
            _currentIcon = icon;
        }

        CheckWarnings(rows);
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

    private void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);

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
        _menu?.Close();
        _about?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        _logo?.Dispose();
    }
}
