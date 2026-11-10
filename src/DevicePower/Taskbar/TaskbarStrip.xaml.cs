using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DevicePower.Devices;

namespace DevicePower.Taskbar;

public partial class TaskbarStrip : Window
{
    public event Action RefreshRequested;
    public event Action RightClickRequested;

    public TaskbarStrip(DeviceMonitor monitor)
    {
        InitializeComponent();
        DataContext = monitor;
    }

    /// <summary>合成模式下的离屏渲染根节点。</summary>
    internal FrameworkElement StripRoot => Chrome;

    /// <summary>窗口回退模式下的悬停提示。</summary>
    internal void SetTooltipText(string text)
    {
        if (Chrome.ToolTip as string == text) return;
        Chrome.ToolTip = text;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不抢焦点，也不进 Alt+Tab
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, exStyle | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        RefreshRequested?.Invoke();

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) =>
        RightClickRequested?.Invoke();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
