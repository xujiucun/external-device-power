using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DevicePower.Devices;
using DevicePower.Settings;
using DevicePower.Taskbar;

namespace DevicePower;

public partial class App : Application
{
    private DeviceMonitor _monitor;
    private TrayHost _tray;
    private BatteryPanel _panel;
    private TaskbarDock _dock;
    private DispatcherTimer _fullscreenTimer;
    private bool _panelHiddenByFullscreen;
    private int _panelFsHits;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = AppSettings.Load();

        _monitor = new DeviceMonitor(settings);
        Resources["Look"] = _monitor.Look;
        _panel = new BatteryPanel(_monitor);
        _dock = new TaskbarDock(_monitor);
        _tray = new TrayHost(_monitor, _panel);

        // 开机自启时刷新一遍路径，防止程序搬家后指向旧位置
        if (settings.AutoStart) DevicePower.Settings.AutoStart.Apply(true);

        _monitor.SettingsChanged += OnSettingsChanged;
        _monitor.Start();
        ApplyDisplayMode();

        if (!settings.WakeTipShown)
        {
            // 先落标记再弹：就算这次进程被直接结束，下次启动也不会再弹
            settings.WakeTipShown = true;
            settings.Save();
            Dispatcher.BeginInvoke(new Action(() => new WakeTipWindow().ShowDialog()),
                DispatcherPriority.Background);
        }

        // 电量条由 dock 自己每秒判全屏；浮动面板没有定时器，这里统一盯一下
        _fullscreenTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _fullscreenTimer.Tick += (_, _) => ApplyFullscreenVisibility();
        _fullscreenTimer.Start();
    }

    private void ApplyFullscreenVisibility()
    {
        if (_monitor.Settings.DisplayMode != "panel")
        {
            _panelHiddenByFullscreen = false;
            _panelFsHits = 0;
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(_panel).Handle;
        string fullscreen = _monitor.Settings.HideOnFullscreen
            ? FullscreenWatcher.CoveringReason(hwnd)
            : null;

        if (fullscreen != null)
        {
            // 和电量条同一套判据：截图选区画布也只待几秒，500ms × 10 次才真隐藏
            _panelFsHits++;
            if (_panelFsHits < 10 || !_panel.IsVisible) return;
            _panel.Hide();
            _panelHiddenByFullscreen = true;
            DiagLog.WriteIfChanged("panel", "hidden by " + fullscreen);
            return;
        }

        _panelFsHits = 0;

        if (!_panelHiddenByFullscreen || _panel.IsVisible) return;
        _panel.ShowNearTray();
        _panelHiddenByFullscreen = false;
        DiagLog.WriteIfChanged("panel", "restored (no fullscreen window)");
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(new Action(ApplyDisplayMode));

    private void ApplyDisplayMode()
    {
        // 他手动切换显示方式 = 重新表态，全屏隐藏的重现标记要清掉
        _panelHiddenByFullscreen = false;

        if (_monitor.Settings.DisplayMode == "panel")
        {
            _dock.Hide();
            _panel.ShowNearTray();
        }
        else
        {
            _panel.Hide();
            _dock.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fullscreenTimer?.Stop();
        _dock?.Dispose();
        _tray?.Dispose();
        _monitor?.Stop();
        base.OnExit(e);
    }
}
