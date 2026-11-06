using System.Windows;
using System.Windows.Input;
using DevicePower.Devices;

namespace DevicePower;

public partial class BatteryPanel : Window
{
    private readonly DeviceMonitor _monitor;

    public BatteryPanel(DeviceMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        Rows.ItemsSource = monitor.Rows;
    }

    /// <summary>固定在任务栏上方的右下角。</summary>
    public void ShowNearTray()
    {
        if (!IsVisible) Show();
        UpdateLayout();

        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth;
        Top = workArea.Bottom - ActualHeight;
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var window = new SettingsWindow(_monitor);
        try
        {
            window.Owner = this;
            window.ShowDialog();
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }
}
