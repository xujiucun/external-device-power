using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevicePower;

/// <summary>电量图形：电池 / 条形 / 圆环 / 分段 / 不显示；充电状态可换绿色填充、绿色描边等。</summary>
public sealed class BatteryGlyph : FrameworkElement
{
    private static readonly Brush ChargeBrush = CreateBrush(Color.FromRgb(0x6E, 0xE7, 0xA8));
    private static readonly Brush TrackBrush = CreateBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    private static readonly Duration Transition = new Duration(TimeSpan.FromMilliseconds(200));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(int), typeof(BatteryGlyph),
        new PropertyMetadata(-1, OnLevelChanged));

    public static readonly DependencyProperty AnimatedLevelProperty = DependencyProperty.Register(
        nameof(AnimatedLevel), typeof(double), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
        nameof(AnimationEnabled), typeof(bool), typeof(BatteryGlyph),
        new PropertyMetadata(false, OnAnimationEnabledChanged));

    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
        nameof(Charging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphStyleProperty = DependencyProperty.Register(
        nameof(GlyphStyle), typeof(string), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata("battery", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChargingStyleProperty = DependencyProperty.Register(
        nameof(ChargingStyle), typeof(string), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata("green-fill", FrameworkPropertyMetadataOptions.AffectsRender));

    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool Charging
    {
        get => (bool)GetValue(ChargingProperty);
        set => SetValue(ChargingProperty, value);
    }

    public string GlyphStyle
    {
        get => (string)GetValue(GlyphStyleProperty);
        set => SetValue(GlyphStyleProperty, value);
    }

    public string ChargingStyle
    {
        get => (string)GetValue(ChargingStyleProperty);
        set => SetValue(ChargingStyleProperty, value);
    }

    /// <summary>实际画出来的电量，可能是两个读数之间的中间值。</summary>
    public double AnimatedLevel
    {
        get => (double)GetValue(AnimatedLevelProperty);
        set => SetValue(AnimatedLevelProperty, value);
    }

    /// <summary>宽度补间开关；只动图形，百分比数字始终是原始读数。</summary>
    public bool AnimationEnabled
    {
        get => (bool)GetValue(AnimationEnabledProperty);
        set => SetValue(AnimationEnabledProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BatteryGlyph)d).MoveToLevel((int)e.NewValue);

    private static void OnAnimationEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var glyph = (BatteryGlyph)d;
        if ((bool)e.NewValue) return;

        glyph.BeginAnimation(AnimatedLevelProperty, null);
        glyph.AnimatedLevel = glyph.Level;
    }

    private void MoveToLevel(int value)
    {
        // 动画没开、或是第一次读到：直接落位，不要从 0 滑上来
        if (!AnimationEnabled || AnimatedLevel < 0 || value < 0)
        {
            BeginAnimation(AnimatedLevelProperty, null);
            AnimatedLevel = value;
            return;
        }

        BeginAnimation(AnimatedLevelProperty, new DoubleAnimation(value, Transition)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 6 || h < 4) return;

        bool greenFill = Charging && ChargingStyle == "green-fill";
        bool greenLine = Charging && ChargingStyle == "green-outline";

        Brush fill = greenFill ? ChargeBrush : Brushes.White;
        Brush line = greenLine ? ChargeBrush : Brushes.White;
        Brush track = greenLine ? ChargeBrush : TrackBrush;
        double shown = AnimatedLevel;

        switch (GlyphStyle)
        {
            case "bar":
                DrawBar(dc, w, h, fill, line, track, shown);
                break;
            case "ring":
                DrawRing(dc, w, h, fill, line, track, shown);
                break;
            case "blocks":
                DrawBlocks(dc, w, h, fill, track, shown);
                break;
            case "none":
                break;
            default:
                DrawBattery(dc, w, h, fill, line, shown);
                break;
        }
    }

    private void DrawBattery(DrawingContext dc, double w, double h, Brush fill, Brush line, double shown)
    {
        double bodyWidth = w - 4.5;
        double radius = Math.Min(3.0, h / 4.0);

        dc.DrawRoundedRectangle(null, MakePen(line, 1.4), new Rect(0.7, 0.7, bodyWidth, h - 1.4), radius, radius);
        dc.DrawRoundedRectangle(line, null, new Rect(bodyWidth + 1.4, h * 0.31, 2.4, h * 0.38), 1.1, 1.1);

        if (shown < 0)
        {
            dc.DrawRoundedRectangle(line, null,
                new Rect(bodyWidth * 0.3, h / 2 - 0.7, bodyWidth * 0.4, 1.4), 0.7, 0.7);
            return;
        }

        double innerWidth = bodyWidth - 5.2;
        double fillWidth = innerWidth * Math.Clamp(shown, 0, 100) / 100.0;
        if (fillWidth > 0.4)
        {
            dc.DrawRoundedRectangle(fill, null,
                new Rect(2.6, 2.6, fillWidth, h - 5.2), 1.2, 1.2);
        }
    }

    private void DrawBar(DrawingContext dc, double w, double h, Brush fill, Brush line, Brush track, double shown)
    {
        double radius = Math.Min(3.0, h / 4.0);
        dc.DrawRoundedRectangle(null, MakePen(track, 1.4), new Rect(0.7, 0.7, w - 1.4, h - 1.4), radius, radius);

        if (shown < 0)
        {
            dc.DrawRoundedRectangle(line, null, new Rect(w * 0.3, h / 2 - 0.7, w * 0.4, 1.4), 0.7, 0.7);
            return;
        }

        double innerWidth = w - 5.2;
        double fillWidth = innerWidth * Math.Clamp(shown, 0, 100) / 100.0;
        if (fillWidth > 0.4)
        {
            dc.DrawRoundedRectangle(fill, null, new Rect(2.6, 2.6, fillWidth, h - 5.2), 1.2, 1.2);
        }
    }

    private void DrawRing(DrawingContext dc, double w, double h, Brush fill, Brush line, Brush track, double shown)
    {
        double size = Math.Min(w, h);
        double thickness = Math.Max(2.0, size * 0.17);
        double radius = (size - thickness) / 2;
        var center = new Point(w / 2, h / 2);

        dc.DrawEllipse(null, MakePen(track, thickness), center, radius, radius);
        if (shown < 0)
        {
            dc.DrawRoundedRectangle(line, null, new Rect(center.X - radius * 0.55, center.Y - 0.7, radius * 1.1, 1.4), 0.7, 0.7);
            return;
        }

        double sweep = 359.9 * Math.Clamp(shown, 0, 100) / 100.0;
        if (sweep < 1) return;

        var pen = new Pen(fill, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, Arc(center, radius, -90, sweep));
    }

    private void DrawBlocks(DrawingContext dc, double w, double h, Brush fill, Brush track, double shown)
    {
        const int count = 5;
        double gap = Math.Max(1.5, w * 0.045);
        double segWidth = (w - gap * (count - 1)) / count;
        double radius = Math.Min(2.0, h / 6);

        for (int i = 0; i < count; i++)
        {
            dc.DrawRoundedRectangle(null, MakePen(track, 1.2),
                new Rect(i * (segWidth + gap), 0.7, segWidth, h - 1.4), radius, radius);
        }

        if (shown < 0) return;

        // 动画关着就整格整格填，开着才允许半格
        double filled = AnimationEnabled
            ? Math.Clamp(shown, 0, 100) / 100.0 * w
            : Math.Round(Math.Clamp(shown, 0, 100) * count / 100.0) * (segWidth + gap);

        for (int i = 0; i < count; i++)
        {
            double x = i * (segWidth + gap);
            double inside = filled - x;
            if (inside <= 0.4) continue;

            var rect = new Rect(x, 0.7, segWidth, h - 1.4);
            if (inside >= segWidth)
            {
                dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
                continue;
            }

            dc.PushClip(new RectangleGeometry(new Rect(x, 0, inside, h)));
            dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            dc.Pop();
        }
    }

    private static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var figure = new PathFigure
        {
            StartPoint = Polar(center, radius, startDegrees),
            IsFilled = false,
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = Polar(center, radius, startDegrees + sweepDegrees),
            Size = new Size(radius, radius),
            IsLargeArc = sweepDegrees > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
