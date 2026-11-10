using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevicePower.Taskbar;

/// <summary>
/// 把电量条的 WPF 视觉树离屏画成预乘 BGRA 像素，交给 <see cref="CompositionStripHost"/> 上传。
/// 电量条窗口本身不显示，只借用它的数据模板和样式。
/// </summary>
internal sealed class StripSurfaceRenderer
{
    private readonly TaskbarStrip _strip;
    private Size _arranged;
    private bool _laidOut;

    public StripSurfaceRenderer(TaskbarStrip strip) => _strip = strip;

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public int Stride { get; private set; }
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();

    /// <summary>重新排版并渲染一帧。scale 是任务栏的 DPI 缩放（1.0 = 96 DPI）。</summary>
    public bool Render(double scale)
    {
        FrameworkElement root = _strip.StripRoot;
        if (root == null) return false;

        // 窗口没有显示过，布局系统不会自己跑，这里手动量一次；数据变化会让 DesiredSize 重新计算
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size desired = root.DesiredSize;
        if (desired.Width < 1 || desired.Height < 1) return false;

        if (!_laidOut || desired != _arranged)
        {
            root.Arrange(new Rect(desired));
            _arranged = desired;
            _laidOut = true;
        }

        SnapGlyphs(root);
        int width = Math.Max(1, (int)Math.Ceiling(desired.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(desired.Height * scale));

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        int stride = width * 4;
        int length = stride * height;
        if (Pixels.Length < length) Pixels = new byte[length];

        bitmap.CopyPixels(Pixels, stride, 0);

        PixelWidth = width;
        PixelHeight = height;
        Stride = stride;
        return true;
    }

    /// <summary>离屏树没有渲染时钟，充电补间不会走，这里让电量图形直接跟到最新读数。</summary>
    private static void SnapGlyphs(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is BatteryGlyph glyph) glyph.SnapToLevel();
            SnapGlyphs(child);
        }
    }
}
