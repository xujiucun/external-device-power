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

        // 开机时还没有设备行，按空面板定的位；行出来后面板变宽，不重新定位就会溢出屏幕右边
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    /// <summary>固定在任务栏上方的右下角。</summary>
    public void ShowNearTray()
    {
        if (!IsVisible) Show();
        UpdateLayout();
        PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;

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
