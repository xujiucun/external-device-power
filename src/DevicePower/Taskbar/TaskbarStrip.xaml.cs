using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DevicePower.Devices;

namespace DevicePower.Taskbar;

public partial class TaskbarStrip : Window
{
    public event Action RefreshRequested;

    public TaskbarStrip(DeviceMonitor monitor)
    {
        InitializeComponent();
        DataContext = monitor;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不抢焦点，也不进 Alt+Tab
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, exStyle | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) =>
        Chrome.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    private void Window_MouseLeave(object sender, MouseEventArgs e) =>
        Chrome.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        RefreshRequested?.Invoke();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
