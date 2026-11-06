using System;
using System.Windows;
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
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(new Action(ApplyDisplayMode));

    private void ApplyDisplayMode()
    {
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
        _dock?.Dispose();
        _tray?.Dispose();
        _monitor?.Stop();
        base.OnExit(e);
    }
}
