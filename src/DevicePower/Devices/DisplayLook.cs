using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DevicePower.Settings;

namespace DevicePower.Devices;

/// <summary>把外观设置换算成界面能直接绑定的值；设置保存后由 DeviceMonitor 刷新。</summary>
public sealed class DisplayLook : INotifyPropertyChanged
{
    private static readonly FontFamily Family = new("Segoe UI, Microsoft YaHei UI");

    private Typeface _face = new Typeface(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private string[] _names = Array.Empty<string>();

    private const double StripNameBase = 11.5;
    private const double StripPercentBase = 12.5;
    private const double StripRowBase = 19;
    private const double StripGlyphWBase = 24;
    private const double StripGlyphHBase = 13;
    private const double PanelNameBase = 15;
    private const double PanelPercentBase = 21;
    private const double PanelRowBase = 32;
    private const double PanelGlyphWBase = 34;
    private const double PanelGlyphHBase = 17;

    /// <summary>任务栏侧的放大只按可用高度封顶：两行时每行放不下就会顶到任务栏外面。</summary>
    private const double StripPaddingDip = 4;

    private double _stripAvailableDip = 48;
    private int _stripRows = 2;

    public DisplayLook(AppSettings settings)
    {
        Settings = settings;
        Refresh();
    }

    public AppSettings Settings { get; }

    /// <summary>由 TaskbarDock 回填任务栏可用高度（DIP）和一列几行，变了才重算。</summary>
    public void SetStripLimit(double availableDip, int rows)
    {
        rows = Math.Clamp(rows, 1, 2);
        if (rows == _stripRows && Math.Abs(availableDip - _stripAvailableDip) < 0.5) return;

        _stripAvailableDip = availableDip;
        _stripRows = rows;
        Refresh();
    }

    /// <summary>设备名列表变了要重新量列宽，否则名字列会按旧名字留白或截断。</summary>
    public void SetNames(IEnumerable<string> names)
    {
        var list = new List<string>(names);
        if (list.Count == _names.Length)
        {
            bool same = true;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != _names[i]) { same = false; break; }
            }

            if (same) return;
        }

        _names = list.ToArray();
        Refresh();
    }

    public Brush TextBrush { get; private set; } = Brushes.White;
    public Brush ChargeBrush { get; private set; } = Brush(Color.FromRgb(0x6E, 0xE7, 0xA8));
    public FontWeight NameWeight { get; private set; } = FontWeights.SemiBold;
    public Visibility NameVisibility { get; private set; } = Visibility.Visible;
    public int NameColumn { get; private set; }
    public int GlyphColumn { get; private set; } = 1;
    public string GlyphStyle { get; private set; } = "battery";
    public string ChargingStyle { get; private set; } = "green-fill";
    public bool GlyphAnimation { get; private set; }

    public double StripNameFontSize { get; private set; } = StripNameBase;
    public double StripPercentFontSize { get; private set; } = StripPercentBase;
    public double StripRowHeight { get; private set; } = StripRowBase;
    public double StripGlyphWidth { get; private set; } = StripGlyphWBase;
    public double StripGlyphHeight { get; private set; } = StripGlyphHBase;
    public Thickness StripGlyphMargin { get; private set; } = new(0, 0, 5, 0);

    /// <summary>固定列宽：改名或读数变长时内容不再重新排，避免整条左右弹。</summary>
    public GridLength StripSlotOne { get; private set; } = new GridLength(64);
    public GridLength StripSlotTwo { get; private set; } = GridLength.Auto;
    public GridLength StripPercentSlot { get; private set; } = new GridLength(44);

    public double PanelNameFontSize { get; private set; } = PanelNameBase;
    public double PanelPercentFontSize { get; private set; } = PanelPercentBase;
    public double PanelRowHeight { get; private set; } = PanelRowBase;
    public double PanelGlyphWidth { get; private set; } = PanelGlyphWBase;
    public double PanelGlyphHeight { get; private set; } = PanelGlyphHBase;
    public Thickness PanelGlyphMargin { get; private set; } = new(0, 0, 10, 0);
    public GridLength PanelSlotOne { get; private set; } = new GridLength(88);
    public GridLength PanelSlotTwo { get; private set; } = GridLength.Auto;
    public GridLength PanelPercentSlot { get; private set; } = new GridLength(62);

    public void Refresh()
    {
        double scale = Math.Clamp(Settings.TextScale, 0.8, 1.6);
        double rowCap = Math.Clamp(Math.Floor((_stripAvailableDip - StripPaddingDip) / _stripRows), 16, 44);
        double stripScale = Math.Min(scale, rowCap / StripRowBase);

        TextBrush = Brush(ParseColor(Settings.TextColor));
        ChargeBrush = Brush(Color.FromRgb(0x6E, 0xE7, 0xA8));
        NameWeight = Settings.TextWeight switch
        {
            "light" => FontWeights.Light,
            "normal" => FontWeights.Normal,
            "bold" => FontWeights.Bold,
            _ => FontWeights.SemiBold,
        };

        _face = new Typeface(Family, FontStyles.Normal, NameWeight, FontStretches.Normal);

        GlyphStyle = Settings.GlyphStyle;
        ChargingStyle = Settings.ChargingStyle;
        GlyphAnimation = Settings.GlyphAnimation;
        bool hideName = Settings.Layout == "name-hidden";
        bool glyphLeft = Settings.Layout == "glyph-first" || hideName;
        NameVisibility = hideName ? Visibility.Collapsed : Visibility.Visible;
        NameColumn = glyphLeft ? 1 : 0;
        GlyphColumn = glyphLeft ? 0 : 1;

        bool noGlyph = GlyphStyle == "none";
        StripNameFontSize = Round(StripNameBase * stripScale);
        StripPercentFontSize = Round(StripPercentBase * stripScale);
        StripRowHeight = Math.Round(StripRowBase * stripScale);
        StripGlyphWidth = noGlyph ? 0 : Round(StripGlyphWBase * stripScale);
        StripGlyphHeight = noGlyph ? 0 : Round(StripGlyphHBase * stripScale);
        StripGlyphMargin = noGlyph ? new Thickness(0) : new Thickness(0, 0, Round(5 * stripScale), 0);

        PanelNameFontSize = Round(PanelNameBase * scale);
        PanelPercentFontSize = Round(PanelPercentBase * scale);
        PanelRowHeight = Math.Round(PanelRowBase * scale);
        PanelGlyphWidth = noGlyph ? 0 : Round(PanelGlyphWBase * scale);
        PanelGlyphHeight = noGlyph ? 0 : Round(PanelGlyphHBase * scale);
        PanelGlyphMargin = noGlyph ? new Thickness(0) : new Thickness(0, 0, Round(10 * scale), 0);

        double stripNameInk = 0;
        double panelNameInk = 0;
        if (!hideName)
        {
            foreach (string name in _names)
            {
                stripNameInk = Math.Max(stripNameInk, Measure(name, StripNameFontSize));
                panelNameInk = Math.Max(panelNameInk, Measure(name, PanelNameFontSize));
            }
        }

        double stripNameWidth = Math.Max(stripNameInk + 10, 30);
        StripSlotOne = glyphLeft ? GridLength.Auto : new GridLength(stripNameWidth);
        StripSlotTwo = glyphLeft && !hideName ? new GridLength(stripNameWidth) : GridLength.Auto;
        // 名称和百分比用同一套字重，量宽度也用 _face，否则会挤成省略号
        StripPercentSlot = new GridLength(Measure("100%", StripPercentFontSize, _face) + 4);

        double panelNameWidth = Math.Max(panelNameInk + 20, 44);
        PanelSlotOne = glyphLeft ? GridLength.Auto : new GridLength(panelNameWidth);
        PanelSlotTwo = glyphLeft && !hideName ? new GridLength(panelNameWidth) : GridLength.Auto;
        PanelPercentSlot = new GridLength(Measure("100%", PanelPercentFontSize, _face) + 4);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private double Measure(string text, double em) => Measure(text, em, _face);

    private static double Measure(string text, double em, Typeface face)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var text0 = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face, em, Brushes.White, 1.0);
        return Math.Ceiling(text0.WidthIncludingTrailingWhitespace);
    }

    private static double Round(double value) => Math.Round(value * 2) / 2;

    private static Color ParseColor(string hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                return c;
        }
        catch (FormatException)
        {
        }

        return Color.FromRgb(0xF2, 0xF2, 0xF2);
    }

    private static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
