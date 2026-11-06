using System;
using System.Collections.Generic;
using DevicePower.Hid;

namespace DevicePower.Devices;

/// <summary>
/// 迈从 2.4G 接收器（05AC:024F）私有 HID 协议。
/// 请求：13 4A 01 + 16×00 + checksum；应答：13 4A 01 00 02 [level] [full|charge] … checksum。
/// </summary>
internal sealed class MchoseConnection : HidDeviceConnection
{
    private const byte ReportId = 0x13;
    private const byte Command = 0x4A;
    private const ushort UsagePage = 0xFF02;
    private const int ReplyTimeoutMs = 1500;

    private readonly DeviceDescriptor _device;

    public MchoseConnection(HidCollection collection, string name) : base(collection)
    {
        _device = new DeviceDescriptor(
            $"mchose:{collection.Vid:X4}:{collection.Pid:X4}",
            "mchose",
            string.IsNullOrWhiteSpace(name) ? "K99" : name.Trim());
    }

    public static bool Matches(HidCollection collection) =>
        collection.Vid == 0x05AC &&
        collection.Pid == 0x024F &&
        collection.UsagePage == UsagePage &&
        collection.OutReportLen > 0;

    public override IReadOnlyList<DeviceDescriptor> Discover() => new[] { _device };

    public override BatteryReading? Read(DeviceDescriptor device)
    {
        HidStream stream = Stream;
        if (stream == null) return null;

        byte[] request = BuildRequest(stream.OutLen);
        if (!stream.Write(request, 800))
        {
            DropStream();
            return null;
        }

        var buffer = new byte[Math.Max(20, stream.InLen)];
        long deadline = Environment.TickCount64 + ReplyTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            int remain = (int)(deadline - Environment.TickCount64);
            long attemptStart = Environment.TickCount64;
            bool ok = stream.TryRead(buffer, Math.Min(remain, 300), out int read);

            if (!ok)
            {
                // 立刻失败 = 句柄已失效；超时失败则继续等到截止时间
                if (Environment.TickCount64 - attemptStart < 20)
                {
                    DropStream();
                    return null;
                }
                continue;
            }

            if (read > 0 && Parse(buffer, read) is BatteryReading reading) return reading;
        }

        return null;
    }

    private static byte[] BuildRequest(int outLength)
    {
        int length = Math.Max(20, outLength);
        var frame = new byte[length];
        frame[0] = ReportId;
        frame[1] = Command;
        frame[2] = 0x01;
        frame[length - 1] = Checksum(frame, length - 1);
        return frame;
    }

    private static BatteryReading? Parse(byte[] report, int length)
    {
        if (length < 7 || report[0] != ReportId || report[1] != Command) return null;
        if (Checksum(report, length - 1) != report[length - 1]) return null;

        int level = report[5];
        if (level > 100) return null;

        // 实测：充电中 report[6] = 0x10，未充电 = 0x01；高半字节为充电标志。
        return new BatteryReading(level, (report[6] & 0xF0) != 0);
    }

    private static byte Checksum(byte[] data, int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }
}
