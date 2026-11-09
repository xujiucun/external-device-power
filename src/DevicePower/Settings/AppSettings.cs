using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevicePower.Settings;

public sealed class AppSettings
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevicePower");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    /// <summary>MCHOSE 接收器的默认名称，作为没单独命名时的回退。</summary>
    public string DeviceName { get; set; } = "K99";

    /// <summary>按设备 Key 保存的自定义名称。</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new();

    public int PollSeconds { get; set; } = 5;
    public int LowThreshold { get; set; } = 20;
    public int CriticalThreshold { get; set; } = 10;
    public bool AutoStart { get; set; }

    /// <summary>taskbar = 任务栏内；panel = 浮动面板。</summary>
    public string DisplayMode { get; set; } = "taskbar";

    /// <summary>任务栏内显示位置：left / right。</summary>
    public string TaskbarSide { get; set; } = "right";

    /// <summary>自定义托盘图标（.ico）；空 = 使用随程序内置的图标。</summary>
    public string TrayIconPath { get; set; } = "";

    /// <summary>设备名与百分比的常规文字颜色（低电量仍用黄/红告警色）。</summary>
    public string TextColor { get; set; } = "#F2F2F2";

    /// <summary>文字大小倍率，0.8–1.6；任务栏侧最多按 1.3 生效。</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>设备名粗细：light / normal / semibold / bold。</summary>
    public string TextWeight { get; set; } = "semibold";

    /// <summary>排版：name-first / glyph-first / name-hidden。</summary>
    public string Layout { get; set; } = "name-first";

    /// <summary>电量图形样式：battery / bar / ring / blocks / none。</summary>
    public string GlyphStyle { get; set; } = "battery";

    /// <summary>充电状态样式：green-fill / green-outline / green-text / none。</summary>
    public string ChargingStyle { get; set; } = "green-fill";

    /// <summary>电量图形宽度补间：只动图形，不改数字。默认关闭。</summary>
    public bool GlyphAnimation { get; set; }

    /// <summary>前景窗口铺满整块屏（全屏游戏）时隐藏电量 UI，退出后自动回来。</summary>
    public bool HideOnFullscreen { get; set; } = true;

    public static AppSettings Load()
    {
        AppSettings settings = null;
        try
        {
            if (File.Exists(FilePath))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            }
        }
        catch
        {
        }

        settings ??= new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? "K99" : DeviceName.Trim();
        if (DeviceName.Length > 24) DeviceName = DeviceName[..24];

        DeviceNames ??= new Dictionary<string, string>();
        foreach (KeyValuePair<string, string> pair in new List<KeyValuePair<string, string>>(DeviceNames))
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) DeviceNames.Remove(pair.Key);
            else DeviceNames[pair.Key] = pair.Value.Trim().Length > 24
                ? pair.Value.Trim()[..24]
                : pair.Value.Trim();
        }
        PollSeconds = Math.Clamp(PollSeconds, 2, 60);
        LowThreshold = Math.Clamp(LowThreshold, 5, 50);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 1, Math.Min(20, LowThreshold));
        if (DisplayMode != "panel" && DisplayMode != "taskbar") DisplayMode = "taskbar";
        if (TaskbarSide != "left" && TaskbarSide != "right") TaskbarSide = "right";
        TrayIconPath = string.IsNullOrWhiteSpace(TrayIconPath) ? "" : TrayIconPath.Trim();

        TextScale = Math.Clamp(TextScale, 0.8, 1.6);
        if (!IsHexColor(TextColor)) TextColor = "#F2F2F2";
        if (!OneOf(TextWeight, "light", "normal", "semibold", "bold")) TextWeight = "semibold";
        if (!OneOf(Layout, "name-first", "glyph-first", "name-hidden")) Layout = "name-first";
        if (!OneOf(GlyphStyle, "battery", "bar", "ring", "blocks", "none")) GlyphStyle = "battery";
        if (!OneOf(ChargingStyle, "green-fill", "green-outline", "green-text", "none"))
            ChargingStyle = "green-fill";
    }

    private static bool OneOf(string value, params string[] allowed)
    {
        foreach (string item in allowed)
        {
            if (string.Equals(item, value, System.StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Length != 7 && value.Length != 9) return false;
        if (value[0] != '#') return false;

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }

        return true;
    }
}
