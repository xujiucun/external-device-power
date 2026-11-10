using System.ComponentModel;
using System.Windows.Media;

namespace DevicePower.Devices;

public sealed class DeviceRow : INotifyPropertyChanged
{
    private static readonly Brush LowBrush = Brush(Color.FromRgb(0xFF, 0xC8, 0x57));
    private static readonly Brush CriticalBrush = Brush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private string _name;
    private int _level = -1;
    private bool _isCharging;
    private bool _isAsleep;
    private Brush _percentBrush = Brushes.White;

    public DeviceRow(string key, string family, string defaultName, string name)
    {
        Key = key;
        Family = family;
        DefaultName = defaultName;
        _name = name;
    }

    public string Key { get; }

    public string Family { get; }

    public string DefaultName { get; }

    public string FamilyLabel => Family switch
    {
        "mchose" => "MCHOSE",
        "logitech" => "罗技",
        _ => Family,
    };

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>设备读数，-1 表示还没读到。</summary>
    public int Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(PercentText));
        }
    }

    /// <summary>设备是否正在充电。</summary>
    public bool IsCharging
    {
        get => _isCharging;
        set
        {
            if (_isCharging == value) return;
            _isCharging = value;
            OnPropertyChanged(nameof(IsCharging));
        }
    }

    public string PercentText => _level < 0 ? "--" : _level + "%";

    /// <summary>设备休眠：显示的是上次读数，界面上会压暗。</summary>
    public bool IsAsleep
    {
        get => _isAsleep;
        set
        {
            if (_isAsleep == value) return;
            _isAsleep = value;
            OnPropertyChanged(nameof(IsAsleep));
        }
    }

    public Brush PercentBrush
    {
        get => _percentBrush;
        private set
        {
            if (ReferenceEquals(_percentBrush, value)) return;
            _percentBrush = value;
            OnPropertyChanged(nameof(PercentBrush));
        }
    }

    public void ApplyVisuals(DisplayLook look)
    {
        int low = look.Settings.LowThreshold;
        int critical = look.Settings.CriticalThreshold;

        if (_level < 0) PercentBrush = look.TextBrush;
        else if (_level <= critical) PercentBrush = CriticalBrush;
        else if (_level <= low) PercentBrush = LowBrush;
        else if (_isCharging && look.ChargingStyle == "green-text") PercentBrush = look.ChargeBrush;
        else PercentBrush = look.TextBrush;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
