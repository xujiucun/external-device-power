using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MchoseProbe;

internal static class Program
{
    private const ushort TargetVid = 0x05AC;
    private const ushort TargetPid = 0x024F;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MCHOSE 2.4G 电量探针 ===");
        Console.WriteLine("提示：键盘休眠时不响应命令，请先按一下键再运行。\n");

        List<HidCollection> all = HidNative.Enumerate();
        Console.WriteLine($"共枚举到 {all.Count} 个 HID 集合：");
        foreach (HidCollection c in all)
        {
            string mark = c.Vid == TargetVid && c.Pid == TargetPid ? "*" : " ";
            string caps = c.CapsError.Length > 0 ? $"  [{c.CapsError}]" : "";
            Console.WriteLine($"  {mark} {c.Vid:X4}:{c.Pid:X4} page=0x{c.UsagePage:X4} usage=0x{c.Usage:X4} in={c.InReportLen} out={c.OutReportLen} '{c.Product}'{caps}");
        }

        List<HidCollection> targets = all.Where(x => x.Vid == TargetVid && x.Pid == TargetPid).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine("\n没有找到目标设备，接收器是否插好？");
            return 2;
        }

        foreach (HidCollection c in targets)
        {
            Console.WriteLine($"\n===== 集合 page=0x{c.UsagePage:X4} (in={c.InReportLen} out={c.OutReportLen}) =====");
            using HidStream dev = HidNative.Open(c);
            if (dev == null) continue;
            if (dev.OutLen == 0)
            {
                Console.WriteLine("  没有输出报告，跳过。");
                continue;
            }

            var received = new List<byte[]>();

            Console.Write("  请现在按一下键盘任意键唤醒，4 秒后发送… ");
            Console.Out.Flush();
            Thread.Sleep(4000);
            Console.WriteLine();

            SendHex(dev, "13 4A 01", "getBattery（2.4G）");
            Collect(dev, 3000, received);
            Report("0x13 应答", received);
            received.Clear();

            Console.WriteLine("  （再发一次，排除首次唤醒丢包）");
            SendHex(dev, "13 4A 01", "getBattery 重试");
            Collect(dev, 3000, received);
            Report("0x13 应答#2", received);
        }

        Console.WriteLine("\n完成。");
        return 0;
    }

    private static void SendHex(HidStream dev, string hex, string label)
    {
        byte[] bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => Convert.ToByte(x, 16)).ToArray();

        // 官方 readCommand：19 字节命令帧，末字节为前面所有字节之和（十进制累加取一字节）
        byte[] frame = new byte[dev.OutLen];
        Array.Copy(bytes, frame, Math.Min(bytes.Length, frame.Length - 1));
        int checksum = frame.Take(frame.Length - 1).Sum(b => (int)b) & 0xFF;
        frame[^1] = (byte)checksum;

        bool ok = dev.Write(frame, 2000);
        Console.WriteLine($"  -> {label}  frame=[{ToHex(frame)}]  checksum=0x{checksum:X2}  写入={ok}");
    }

    private static void Send(HidStream dev, byte reportId, byte[] payload, string label)
    {
        byte[] frame = new byte[dev.OutLen];
        frame[0] = reportId;
        Array.Copy(payload, 0, frame, 1, Math.Min(payload.Length, frame.Length - 1));

        bool ok = dev.Write(frame, 2000);
        Console.WriteLine($"  -> {label}  report=0x{reportId:X2}  frame=[{ToHex(frame)}]  写入={ok}");
    }

    private static void Collect(HidStream dev, int durationMs, List<byte[]> sink)
    {
        if (dev.InLen == 0) return;
        var buffer = new byte[dev.InLen];
        long deadline = Environment.TickCount64 + durationMs;
        while (Environment.TickCount64 < deadline)
        {
            int remain = (int)(deadline - Environment.TickCount64);
            if (dev.TryRead(buffer, Math.Min(remain, 300), out int read) && read > 0)
            {
                byte[] copy = new byte[read];
                Array.Copy(buffer, copy, read);
                sink.Add(copy);
            }
        }
    }

    private static void Report(string title, List<byte[]> reports)
    {
        if (reports.Count == 0)
        {
            Console.WriteLine($"  <== {title}：无回包");
            return;
        }

        foreach (byte[] r in reports)
        {
            Console.WriteLine($"  <== {title} report=0x{r[0]:X2} len={r.Length}  [{ToHex(r)}]");
            Explain(r);
        }
    }

    private static void Explain(byte[] r)
    {
        // 应答分包：[reportId=0x13][cmd][total][seq][yy][14 字节数据][checksum]
        if (r.Length < 7 || r[0] != 0x13) return;

        int sum = r.Take(r.Length - 1).Sum(b => b) & 0xFF;
        bool checksumOk = sum == r[^1];

        int level = r[5];
        int full = (r[6] >> 4) & 0xF;
        int charge = r[6] & 0xF;
        Console.WriteLine($"        解析: 电量={level}%  fullStatus={full}  chargeStatus={charge}  校验和={(checksumOk ? "OK" : $"FAIL(算得{sum:X2} 实际{r[^1]:X2})")}");
    }

    private static string ToHex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));
}
