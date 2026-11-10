using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevicePower.Taskbar;

/// <summary>
/// 电量条的悬停提示。不用系统 tooltip（任务栏子窗口上 TTM_ADDTOOL 会被拒绝），
/// 自己弹一个不激活、鼠标穿透的小窗口，样式和右键菜单保持一致。
/// </summary>
internal sealed class StripTooltipWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private readonly TextBlock _text;

    public StripTooltipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        IsHitTestVisible = false;
        Left = -20000;
        Top = -20000;

        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 12.5,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _text,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不抢焦点、不接收鼠标，提示挡在电量条上也不会打断悬停
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExNoactivate | WsExToolwindow | WsExTransparent);
    }

    /// <summary>显示或更新提示；位置在电量条上方，放不下就翻到下方。</summary>
    public void ShowText(string text, double scale, int stripLeft, int stripTop, int stripWidth, int stripHeight)
    {
        if (_text.Text != text) _text.Text = text;
        if (!IsVisible) Show();
        UpdateLayout();

        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));

        System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(stripLeft, stripTop, Math.Max(1, stripWidth), Math.Max(1, stripHeight))).WorkingArea;

        int x = stripLeft + stripWidth - width;
        int y = stripTop - height - 6;
        if (y < screen.Top) y = stripTop + stripHeight + 6;
        if (x < screen.Left) x = screen.Left;
        if (x + width > screen.Right) x = screen.Right - width;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, SwpNoActivate);
    }

    public void HideTip()
    {
        if (IsVisible) Hide();
    }
}
