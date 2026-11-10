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

    // 05AC:024F 是本机实测的 K99 V2 接收器；3554:FA09 是社区（Voltide）验证过的另一版接收器，
    // 帧格式相同（report 0x13 / 0x4A / 20 字节 / 末字节校验），只是 VID/PID 不同。
    private const ushort MchoseVid = 0x05AC;
    private const ushort MchosePid = 0x024F;
    private const ushort AlternateVid = 0x3554;
    private const ushort AlternatePid = 0xFA09;

    private readonly DeviceDescriptor _device;

    public MchoseConnection(HidCollection collection, string name) : base(collection)
    {
        string custom = name?.Trim();
        _device = new DeviceDescriptor(
            $"mchose:{collection.Vid:X4}:{collection.Pid:X4}",
            "mchose",
            string.IsNullOrWhiteSpace(custom) ? DefaultName(collection) : custom);
    }

    public static bool Matches(HidCollection collection)
    {
        if (collection.UsagePage != UsagePage || collection.OutReportLen == 0) return false;

        if (collection.Vid == MchoseVid && collection.Pid == MchosePid) return true;

        // 另一版接收器按 Voltide 的结论只认 20 字节的厂商集合
        return collection.Vid == AlternateVid &&
               collection.Pid == AlternatePid &&
               collection.OutReportLen == 20;
    }

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

        // 接收器会先把查询帧原样回显一遍；只有 [2]=01 [3]=00 [4]=02 才是真正的电量应答，
        // 不校验这几位会把回显里的 0 当成 0% 读数。
        if (report[2] != 0x01 || report[3] != 0x00 || report[4] != 0x02) return null;

        if (Checksum(report, length - 1) != report[length - 1]) return null;

        int level = report[5];
        if (level > 100) return null;

        // 实测：充电中 report[6] = 0x10，未充电 = 0x01；高半字节为充电标志。
        return new BatteryReading(level, (report[6] & 0xF0) != 0);
    }

    /// <summary>没有自定义名称时优先用接收器上报的产品名，其次按 PID 给出已知型号。</summary>
    private static string DefaultName(HidCollection collection)
    {
        string product = (collection.Product ?? "").Trim();
        if (product.Length > 0 &&
            !product.Equals("2.4G Wireless Receiver", StringComparison.OrdinalIgnoreCase))
        {
            if (product.StartsWith("MCHOSE ", StringComparison.OrdinalIgnoreCase))
                product = product[7..].Trim();
            if (product.Length > 0) return product;
        }

        return collection.Pid == AlternatePid ? "K99 v2" : "K99";
    }

    private static byte Checksum(byte[] data, int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }
}
