using System;
using System.Collections.Generic;
using System.Threading;
using DevicePower.Hid;

namespace DevicePower.Devices;

/// <summary>
/// 罗技 Lightspeed / Unifying 接收器的 HID++ 2.0 电量。
/// 优先 0x1000（协议里直接给出百分比），没有该 feature 时退回 0x1004 的 function 1。
/// 0x1004 function 0 返回的是电压常量，且会被 G HUB 当成自己的状态，绝不能请求。
/// </summary>
internal sealed class LogitechConnection : HidDeviceConnection
{
    private const ushort LogitechVid = 0x046D;
    private const ushort UsagePage = 0xFF00;
    private const ushort Usage = 0x0002;

    private const ushort FeatureLevelStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1004;

    private const byte ReportLong = 0x11;
    private const byte SwId = 0x0E;
    private const byte FuncRootGetFeature = 0x00;
    private const byte FuncRootPing = 0x01;
    private const byte FuncLevelGet = 0x00;
    private const byte FuncBatteryLevel = 0x01;

    private const int WakeDeadlineMs = 1100;
    private const int ScanBudgetMs = 3600;
    private const byte MaxScanIndex = 4;
    private const int RescanDelayMs = 30000;

    private readonly List<DeviceDescriptor> _devices = new();
    private readonly Dictionary<byte, byte> _features = new();
    private readonly Dictionary<byte, byte> _functions = new();
    private long _nextScan;

    public LogitechConnection(HidCollection collection) : base(collection) { }

    public static bool Matches(HidCollection collection) =>
        collection.Vid == LogitechVid &&
        collection.UsagePage == UsagePage &&
        collection.Usage == Usage &&
        collection.OutReportLen == 20;

    public override IReadOnlyList<DeviceDescriptor> Discover()
    {
        if (_devices.Count > 0) return _devices;

        long now = Environment.TickCount64;
        if (now < _nextScan) return _devices;
        _nextScan = now + RescanDelayMs;

        HidStream stream = Stream;
        if (stream == null) return _devices;

        Drain(stream, 40);

        long deadline = now + ScanBudgetMs;
        for (byte slot = 1; slot <= MaxScanIndex; slot++)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (left <= 150) break;

            if (!Ping(stream, slot, Math.Min(WakeDeadlineMs, left))) continue;

            _devices.Add(new DeviceDescriptor(
                $"logi:{Collection.Vid:X4}:{Collection.Pid:X4}#{slot}",
                "logitech",
                DefaultName(slot),
                slot));
        }

        return _devices;
    }

    public override BatteryReading? Read(DeviceDescriptor device)
    {
        HidStream stream = Stream;
        if (stream == null) return null;

        if (!_features.TryGetValue(device.Slot, out byte feature) ||
            !_functions.TryGetValue(device.Slot, out byte function))
        {
            if (!ResolveBatteryFeature(stream, device.Slot, out feature, out function)) return null;
            _features[device.Slot] = feature;
            _functions[device.Slot] = function;
        }

        byte[] response = Call(stream, device.Slot, feature, function);
        if (response == null) return null;

        int level = response[4];
        if (level > 100) return null;

        return new BatteryReading(level, (response[5] & 0x02) != 0);
    }

    private static bool ResolveBatteryFeature(HidStream stream, byte slot, out byte feature, out byte function)
    {
        feature = 0;
        function = 0;

        byte levelFeature = GetFeature(stream, slot, FeatureLevelStatus, out bool responded);
        if (levelFeature != 0)
        {
            feature = levelFeature;
            function = FuncLevelGet;
            return true;
        }

        // 完全没有回包 = 设备在休眠；回了 feature 0 才是“设备不支持”。
        if (!responded) return false;

        byte voltageFeature = GetFeature(stream, slot, FeatureBatteryVoltage, out responded);
        if (voltageFeature == 0) return false;

        feature = voltageFeature;
        function = FuncBatteryLevel;
        return true;
    }

    private string DefaultName(byte slot)
    {
        string product = (Collection.Product ?? "").Trim();
        bool generic = product.Length == 0 ||
                       product.IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       product.IndexOf("usb", StringComparison.OrdinalIgnoreCase) >= 0;

        string name = generic ? "罗技设备" : product;
        if (name.Length > 24) name = name[..24];
        return slot > 1 ? $"{name} {slot}" : name;
    }

    private static bool Ping(HidStream stream, byte slot, int deadlineMs)
    {
        byte[] request = NewRequest(slot, 0x00, FuncRootPing);
        if (!stream.Write(request, 300)) return false;
        return WaitFor(stream, slot, 0x00, request[3], deadlineMs) != null;
    }

    private static byte GetFeature(HidStream stream, byte slot, ushort featureId, out bool responded)
    {
        responded = false;

        // 设备刚从无线休眠醒来时经常吃掉第一个请求，重试一次。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            byte[] request = NewRequest(slot, 0x00, FuncRootGetFeature);
            request[4] = (byte)(featureId >> 8);
            request[5] = (byte)(featureId & 0xFF);

            if (!stream.Write(request, 300))
            {
                Thread.Sleep(120);
                continue;
            }

            byte[] response = WaitFor(stream, slot, 0x00, request[3], attempt == 0 ? 350 : 500);
            if (response != null)
            {
                responded = true;
                return response[4];
            }

            Thread.Sleep(250);
        }

        return 0;
    }

    private static byte[] Call(HidStream stream, byte slot, byte feature, byte function)
    {
        byte[] request = NewRequest(slot, feature, function);
        if (!stream.Write(request, 400)) return null;
        return WaitFor(stream, slot, feature, request[3], 600);
    }

    private static byte[] NewRequest(byte slot, byte feature, byte function)
    {
        var request = new byte[20];
        request[0] = ReportLong;
        request[1] = slot;
        request[2] = feature;
        request[3] = (byte)(((function & 0x0F) << 4) | (SwId & 0x0F));
        return request;
    }

    private static byte[] WaitFor(HidStream stream, byte slot, byte feature, byte functionByte, int deadlineMs)
    {
        var buffer = new byte[Math.Max(20, stream.InLen)];
        long deadline = Environment.TickCount64 + deadlineMs;

        while (Environment.TickCount64 < deadline)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (!stream.TryRead(buffer, Math.Min(left, 100), out int read))
            {
                Thread.Sleep(1);
                continue;
            }

            if (read < 7) continue;
            if (buffer[0] != ReportLong || buffer[1] != slot || buffer[2] != feature || buffer[3] != functionByte) continue;
            if (buffer[3] == 0xFF) continue; // 错误应答，让调用方按超时处理

            var copy = new byte[read];
            Array.Copy(buffer, copy, read);
            return copy;
        }

        return null;
    }

    private static void Drain(HidStream stream, int milliseconds)
    {
        var buffer = new byte[Math.Max(20, stream.InLen)];
        long deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (!stream.TryRead(buffer, Math.Min(left, 20), out _)) break;
        }
    }
}
