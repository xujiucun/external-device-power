using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevicePower.Devices;
using DevicePower.Settings;

namespace DevicePower;

public partial class SettingsWindow : Window
{
    private readonly DeviceMonitor _monitor;
    private readonly ObservableCollection<NameEdit> _nameEdits;
    private readonly Dictionary<string, List<RadioButton>> _groups = new();
    private string _trayIconPath;
    private string _customColor = "#F2F2F2";

    public SettingsWindow(DeviceMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        // 内容比屏幕高时窗口封顶，中间区域滚动，底部按钮始终可见
        MaxHeight = Math.Max(520, Math.Min(SystemParameters.WorkArea.Height - 48, 760));

        BodyScroll.ScrollChanged += (_, _) => UpdateBottomFade();
        Loaded += (_, _) => UpdateBottomFade();

        var settings = monitor.Settings;
        _nameEdits = new ObservableCollection<NameEdit>(
            monitor.Catalog.Select(row => new NameEdit(row, IsShown(row.Key))));
        DeviceList.ItemsSource = _nameEdits;
        monitor.Catalog.CollectionChanged += OnRowsChanged;
        Closed += (_, _) => monitor.Catalog.CollectionChanged -= OnRowsChanged;
        UpdateNoDeviceText();

        IntervalSlider.Value = settings.PollSeconds;
        LowSlider.Value = settings.LowThreshold;
        CriticalSlider.Value = settings.CriticalThreshold;
        AutoStartBox.IsChecked = settings.AutoStart;
        _trayIconPath = settings.TrayIconPath;
        UpdateTrayIconText();
        ModeTaskbar.IsChecked = settings.DisplayMode != "panel";
        ModePanel.IsChecked = settings.DisplayMode == "panel";
        SideRight.IsChecked = settings.TaskbarSide != "left";
        SideLeft.IsChecked = settings.TaskbarSide == "left";
        FullscreenBox.IsChecked = settings.HideOnFullscreen;
        OnlineBox.IsChecked = settings.NotifyOnline;

        IntervalSlider.ValueChanged += (_, _) => UpdateReadouts();
        LowSlider.ValueChanged += (_, _) => UpdateReadouts();
        CriticalSlider.ValueChanged += (_, _) => UpdateReadouts();

        UpdateReadouts();
        UpdateCalibrationText();
        LoadAppearance(settings);
    }

    private void LoadAppearance(AppSettings settings)
    {
        _customColor = settings.TextColor;
        ScaleSlider.Value = Math.Round(settings.TextScale * 100);

        Select("TextWeight", settings.TextWeight);
        Select("Layout", settings.Layout);
        Select("GlyphStyle", settings.GlyphStyle);
        Select("ChargingStyle", settings.ChargingStyle);
        AnimationBox.IsChecked = settings.GlyphAnimation;

        foreach (RadioButton swatch in Group("TextColor"))
        {
            swatch.IsChecked = string.Equals((string)swatch.Tag, settings.TextColor,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText == null) return;
        ScaleText.Text = $"{(int)Math.Round(ScaleSlider.Value)}%";
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        string current = CheckedValue(Group("TextColor"), _customColor);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = ParseDrawingColor(current),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _customColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        foreach (RadioButton swatch in Group("TextColor")) swatch.IsChecked = false;
    }

    private List<RadioButton> Group(string name)
    {
        if (!_groups.TryGetValue(name, out List<RadioButton> list))
        {
            list = new List<RadioButton>();
            Collect(this, name, list);
            _groups[name] = list;
        }

        return list;
    }

    private static void Collect(DependencyObject node, string group, List<RadioButton> into)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is RadioButton radio && radio.GroupName == group) into.Add(radio);
            if (child is DependencyObject dependency) Collect(dependency, group, into);
        }
    }

    private void Select(string group, string value)
    {
        foreach (RadioButton radio in Group(group))
        {
            radio.IsChecked = string.Equals((string)radio.Tag, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string CheckedValue(List<RadioButton> group, string fallback)
    {
        foreach (RadioButton radio in group)
        {
            if (radio.IsChecked == true) return (string)radio.Tag;
        }

        return fallback;
    }

    private static System.Drawing.Color ParseDrawingColor(string hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim()) is System.Windows.Media.Color c)
            {
                return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
        }
        catch (FormatException)
        {
        }

        return System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2);
    }

    private void UpdateReadouts()
    {
        IntervalText.Text = $"{(int)IntervalSlider.Value} 秒";
        LowText.Text = $"{(int)LowSlider.Value}%";
        CriticalText.Text = $"{(int)CriticalSlider.Value}%";
    }

    private void OnRowsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (DeviceRow row in _monitor.Catalog)
        {
            if (_nameEdits.Any(edit => ReferenceEquals(edit.Row, row))) continue;
            _nameEdits.Add(new NameEdit(row, IsShown(row.Key)));
        }

        UpdateNoDeviceText();
    }

    private bool IsShown(string key) =>
        !_monitor.Settings.HiddenDevices.Contains(key, StringComparer.OrdinalIgnoreCase);

    private void UpdateNoDeviceText() =>
        NoDeviceText.Visibility = _nameEdits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateCalibrationText()
    {
        if (_monitor.Catalog.Count == 0)
        {
            CalibrationText.Text = "还没发现设备：插好接收器、唤醒设备后再点「重新读取」。";
            return;
        }

        if (_monitor.Rows.Count == 0)
        {
            bool allHidden = _monitor.Catalog.All(row => !IsShown(row.Key));
            CalibrationText.Text = allHidden
                ? "设备都被隐藏了：勾选上面的「显示」后恢复读数。"
                : "还没读到电量：设备可能在休眠或已断开，唤醒设备后会重新出现。";
            return;
        }

        CalibrationText.Text = string.Join("  ·  ",
            _monitor.Rows.Select(row => $"{row.Name} {row.PercentText}"));
    }

    private async void ReRead_Click(object sender, RoutedEventArgs e)
    {
        ReReadButton.IsEnabled = false;
        CalibrationText.Text = "正在读取…";

        try
        {
            int count = await _monitor.ReadNowAsync();
            UpdateCalibrationText();
            if (count == 0 && _monitor.Rows.Count > 0) CalibrationText.Text += "（没收到回包，设备可能在休眠）";
        }
        finally
        {
            ReReadButton.IsEnabled = true;
        }
    }

    private void UpdateBottomFade()
    {
        bool hasMore = BodyScroll.ScrollableHeight > 1 &&
                       BodyScroll.VerticalOffset < BodyScroll.ScrollableHeight - 1;
        BottomFade.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTrayIconText() =>
        TrayIconText.Text = string.IsNullOrEmpty(_trayIconPath)
            ? "随程序内置"
            : Path.GetFileName(_trayIconPath);

    private void PickTrayIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择托盘图标",
            Filter = "图标文件 (*.ico)|*.ico",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _trayIconPath = dialog.FileName;
            UpdateTrayIconText();
        }
    }

    private void ResetTrayIcon_Click(object sender, RoutedEventArgs e)
    {
        _trayIconPath = "";
        UpdateTrayIconText();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyChanges();
        DialogResult = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyChanges();
        ApplyHint.Text = "已应用 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void ApplyChanges()
    {
        var settings = _monitor.Settings;

        settings.DeviceNames ??= new Dictionary<string, string>();
        foreach (NameEdit edit in _nameEdits)
        {
            string name = (edit.Name ?? "").Trim();
            if (name.Length > 24) name = name[..24];
            if (name.Length == 0) name = edit.Row.DefaultName;

            edit.Row.Name = name;
            settings.DeviceNames[edit.Row.Key] = name;
        }

        DeviceRow mchose = _monitor.Catalog.FirstOrDefault(row => row.Family == "mchose");
        if (mchose != null) settings.DeviceName = mchose.Name;

        settings.HiddenDevices = _nameEdits.Where(edit => !edit.Visible).Select(edit => edit.Row.Key).ToList();

        settings.PollSeconds = (int)IntervalSlider.Value;
        settings.LowThreshold = (int)LowSlider.Value;
        settings.CriticalThreshold = (int)CriticalSlider.Value;
        settings.AutoStart = AutoStartBox.IsChecked == true;
        settings.TrayIconPath = _trayIconPath ?? "";
        settings.DisplayMode = ModePanel.IsChecked == true ? "panel" : "taskbar";
        settings.TaskbarSide = SideLeft.IsChecked == true ? "left" : "right";
        settings.HideOnFullscreen = FullscreenBox.IsChecked == true;
        settings.NotifyOnline = OnlineBox.IsChecked == true;
        settings.TextColor = CheckedValue(Group("TextColor"), _customColor);
        settings.TextScale = Math.Round(ScaleSlider.Value / 100.0, 2);
        settings.TextWeight = CheckedValue(Group("TextWeight"), "semibold");
        settings.Layout = CheckedValue(Group("Layout"), "name-first");
        settings.GlyphStyle = CheckedValue(Group("GlyphStyle"), "battery");
        settings.ChargingStyle = CheckedValue(Group("ChargingStyle"), "green-fill");
        settings.GlyphAnimation = AnimationBox.IsChecked == true;
        settings.Normalize();

        settings.Save();
        DevicePower.Settings.AutoStart.Apply(settings.AutoStart);
        _monitor.ApplySettings();
        UpdateCalibrationText();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // 点在输入框 / 勾选 / 单选 / 滑块 / 按钮 / 滚动条上时不抢拖动，否则没法选字和操作控件；
        // 其余地方（标题、标签、空白）按下即拖，不用只盯标题栏
        for (DependencyObject node = e.OriginalSource as DependencyObject;
             node != null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase ||
                node is System.Windows.Controls.Primitives.ButtonBase ||
                node is System.Windows.Controls.Primitives.RangeBase ||
                node is System.Windows.Controls.Primitives.ScrollBar ||
                node is System.Windows.Controls.Primitives.Thumb) return;
        }

        DragMove();
    }

    private sealed class NameEdit : INotifyPropertyChanged
    {
        private string _name;
        private bool _visible;

        public NameEdit(DeviceRow row, bool visible)
        {
            Row = row;
            _name = row.Name;
            _visible = visible;
        }

        public DeviceRow Row { get; }

        public string FamilyLabel => Row.FamilyLabel;

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
