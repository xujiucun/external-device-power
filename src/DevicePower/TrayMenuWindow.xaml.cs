using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevicePower;

public partial class TrayMenuWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private bool _positioned;
    private bool _closing;

    public event Action SettingsRequested;
    public event Action TogglePanelRequested;
    public event Action RefreshRequested;
    public event Action AboutRequested;
    public event Action ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseMenu();
        };
        Loaded += (_, _) => PositionAtCursor();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void CloseMenu()
    {
        if (_closing) return;
        Close();
    }

    private void PositionAtCursor()
    {
        if (_positioned) return;
        _positioned = true;

        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);

        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

        int x = cursor.X;
        int y = cursor.Y - height;
        if (x + width > screen.Right) x = cursor.X - width;
        if (y < screen.Top) y = cursor.Y;
        if (x < screen.Left) x = screen.Left;

        SetWindowPos(source.Handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
        SetForegroundWindow(source.Handle);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        SettingsRequested?.Invoke();
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        TogglePanelRequested?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        RefreshRequested?.Invoke();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        AboutRequested?.Invoke();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        ExitRequested?.Invoke();
    }
}
