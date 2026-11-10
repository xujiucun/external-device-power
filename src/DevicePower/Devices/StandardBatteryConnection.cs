using System;
using System.Collections.Generic;
using DevicePower.Hid;

namespace DevicePower.Devices;

/// <summary>
/// 标准 HID 电量兜底：接收器直接暴露 Battery Strength（Usage Page 0x06 / Usage 0x20）集合时，
/// 不需要品牌协议，读输入报告或 Feature Report 里的这个用法即可。只给百分比，没有充电标志。
/// </summary>
internal sealed class StandardBatteryConnection : HidDeviceConnection
{
    private const ushort BatteryPage = 0x06;
    private const ushort BatteryLevelUsage = 0x20;
    private const int ReplyTimeoutMs = 1200;

    /// <summary>
    /// 有些设备只在电量变化时才发输入报告，轮询没等到就沿用上次读数；
    /// 接收器被拔掉时集合会直接消失，不会靠缓存继续显示。
    /// </summary>
    private const int CacheTtlMs = 5 * 60 * 1000;

    private readonly DeviceDescriptor _device;
    private int _lastLevel = -1;
    private long _lastSeen;

    public StandardBatteryConnection(HidCollection collection) : base(collection)
    {
        _device = new DeviceDescriptor(
            $"std:{collection.Vid:X4}:{collection.Pid:X4}",
            "standard",
            DefaultName(collection));
    }

    public static bool Matches(HidCollection collection)
    {
        if (!collection.StandardBattery) return false;

        // 这些接收器已经有专门的协议适配，避免同一台设备被报两次
        if (collection.Vid == 0x046D) return false;
        if (collection.Vid == 0x05AC && collection.Pid == 0x024F) return false;
        if (collection.Vid == 0x3554 && collection.Pid == 0xFA09) return false;

        return true;
    }

    public override IReadOnlyList<DeviceDescriptor> Discover() => new[] { _device };

    public override BatteryReading? Read(DeviceDescriptor device)
    {
        HidStream stream = Stream;
        if (stream == null) return null;

        // 有的接收器把电量放在 Feature Report 里，先直接取一次
        if (stream.FeatLen > 0)
        {
            var feature = new byte[stream.FeatLen];
            if (stream.GetFeature(feature) &&
                stream.TryGetUsageValue(true, feature, feature.Length, BatteryPage, BatteryLevelUsage, out int featureValue))
            {
                if (Accept(featureValue) is BatteryReading featureReading) return featureReading;
            }
        }

        // 没有 Feature 就等一次输入报告
        var buffer = new byte[Math.Max(8, stream.InLen)];
        long deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int remain = (int)(deadline - Environment.TickCount64);
            long started = Environment.TickCount64;
            if (!stream.TryRead(buffer, Math.Max(50, Math.Min(remain, 300)), out int read))
            {
                // 立刻失败 = 句柄已失效；超时则继续等到截止时间
                if (Environment.TickCount64 - started < 20)
                {
                    DropStream();
                    return Cached();
                }
                continue;
            }

            if (read > 0 &&
                stream.TryGetUsageValue(false, buffer, read, BatteryPage, BatteryLevelUsage, out int value))
            {
                if (Accept(value) is BatteryReading reading) return reading;
            }
        }

        return Cached();
    }

    /// <summary>标准协议没有缓存以外的状态；这里只接受合法值并记录时间。</summary>
    private BatteryReading? Accept(int value)
    {
        BatteryReading? reading = Normalize(value);
        if (reading is BatteryReading accepted)
        {
            _lastLevel = accepted.Level;
            _lastSeen = Environment.TickCount64;
        }

        return reading;
    }

    private BatteryReading? Cached()
    {
        if (_lastLevel < 0) return null;
        if (Environment.TickCount64 - _lastSeen > CacheTtlMs) return null;
        return new BatteryReading(_lastLevel, false);
    }

    /// <summary>标准规定 0–100；部分接收器按 0–255 上报，这里做一次归一化。</summary>
    private static BatteryReading? Normalize(int value)
    {
        if (value < 0) return null;
        if (value <= 100) return new BatteryReading(value, false);
        if (value <= 255) return new BatteryReading((int)Math.Round(value * 100.0 / 255.0), false);
        return null;
    }

    private static string DefaultName(HidCollection collection)
    {
        string product = (collection.Product ?? "").Trim();
        return product.Length > 0 ? product : "外设";
    }
}
