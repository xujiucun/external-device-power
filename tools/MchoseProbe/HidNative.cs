using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MchoseProbe;

internal sealed class HidCollection
{
    public string Path;
    public ushort Vid, Pid, Version;
    public ushort UsagePage, Usage;
    public ushort InReportLen, OutReportLen, FeatReportLen;
    public string Product = "", Manufacturer = "", Serial = "";
    public string CapsError = "";
}

internal sealed class HidStream : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ManualResetEvent _writeEvent = new(false);
    private readonly ManualResetEvent _readEvent = new(false);

    public int InLen { get; private set; }
    public int OutLen { get; private set; }
    public string Path { get; }

    internal HidStream(SafeFileHandle handle, string path, int inLen, int outLen)
    {
        _handle = handle;
        Path = path;
        InLen = inLen;
        OutLen = outLen;
    }

    public bool Write(byte[] frame, int timeoutMs)
    {
        _writeEvent.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle() };
        bool ok = HidNative.WriteFile(_handle, frame, frame.Length, out int _, ref overlapped);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != HidNative.ERROR_IO_PENDING) return false;
            if (!_writeEvent.WaitOne(timeoutMs))
            {
                HidNative.CancelIoEx(_handle, ref overlapped);
                return false;
            }
        }
        return HidNative.GetOverlappedResult(_handle, ref overlapped, out int _, false);
    }

    public bool TryRead(byte[] buffer, int timeoutMs, out int read)
    {
        read = 0;
        _readEvent.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle() };
        bool ok = HidNative.ReadFile(_handle, buffer, buffer.Length, out _, ref overlapped);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != HidNative.ERROR_IO_PENDING) return false;
            if (!_readEvent.WaitOne(timeoutMs))
            {
                HidNative.CancelIoEx(_handle, ref overlapped);
                return false;
            }
        }
        return HidNative.GetOverlappedResult(_handle, ref overlapped, out read, false);
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _writeEvent.Dispose();
        _readEvent.Dispose();
    }
}

internal static class HidNative
{
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct HIDD_ATTRIBUTES
    {
        [FieldOffset(0)] public int Size;
        [FieldOffset(4)] public ushort VendorID;
        [FieldOffset(6)] public ushort ProductID;
        [FieldOffset(8)] public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, IntPtr devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, IntPtr devInst, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, IntPtr buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle h, IntPtr buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, IntPtr buffer, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int count, out int written, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int count, out int read, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(SafeFileHandle h, ref NativeOverlapped overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIoEx(SafeFileHandle h, ref NativeOverlapped overlapped);

    internal const int ERROR_IO_PENDING = 997;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const uint FILE_SHARE_RW = 0x1 | 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint GENERIC_READ_WRITE = 0x80000000 | 0x40000000;

    internal static List<HidCollection> Enumerate()
    {
        var result = new List<HidCollection>();
        HidD_GetHidGuid(out Guid hidGuid);

        int cr = CM_Get_Device_Interface_List_SizeW(out uint length, ref hidGuid, IntPtr.Zero, 0);
        if (cr != 0)
        {
            Console.WriteLine($"CM_Get_Device_Interface_List_Size 失败 CONFIGRET={cr}");
            return result;
        }

        var buffer = new char[length];
        cr = CM_Get_Device_Interface_ListW(ref hidGuid, IntPtr.Zero, buffer, length, 0);
        if (cr != 0)
        {
            Console.WriteLine($"CM_Get_Device_Interface_List 失败 CONFIGRET={cr}");
            return result;
        }

        foreach (string path in new string(buffer).TrimEnd('\0').Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            HidCollection info = Inspect(path);
            if (info != null) result.Add(info);
        }

        return result;
    }

    private static HidCollection Inspect(string path)
    {
        // 属性查询只需要 0 访问权限；对键盘/鼠标集合申请读写会被拒绝
        SafeFileHandle handle = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"    打开失败 err={Marshal.GetLastWin32Error()}  {path}");
            return null;
        }

        using (handle)
        {
            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes)) return null;

            var info = new HidCollection
            {
                Path = path,
                Vid = attributes.VendorID,
                Pid = attributes.ProductID,
                Version = attributes.VersionNumber,
                Product = ReadString(handle, 0),
                Manufacturer = ReadString(handle, 1),
                Serial = ReadString(handle, 2),
            };

            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
            {
                info.CapsError = "GetPreparsedData 失败";
                return info;
            }

            try
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                int status = HidP_GetCaps(preparsed, ref caps);
                if (status == HIDP_STATUS_SUCCESS)
                {
                    info.UsagePage = caps.UsagePage;
                    info.Usage = caps.Usage;
                    info.InReportLen = caps.InputReportByteLength;
                    info.OutReportLen = caps.OutputReportByteLength;
                    info.FeatReportLen = caps.FeatureReportByteLength;
                }
                else
                {
                    info.CapsError = $"HidP_GetCaps=0x{status:X8}";
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }

            return info;
        }
    }

    private static string ReadString(SafeFileHandle handle, int which)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            bool ok = which switch
            {
                0 => HidD_GetProductString(handle, buffer, 512),
                1 => HidD_GetManufacturerString(handle, buffer, 512),
                _ => HidD_GetSerialNumberString(handle, buffer, 512),
            };
            return ok ? Marshal.PtrToStringUni(buffer) : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static HidStream Open(HidCollection collection)
    {
        SafeFileHandle handle = CreateFileW(collection.Path, GENERIC_READ_WRITE, FILE_SHARE_RW,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"    打开失败 err={Marshal.GetLastWin32Error()}");
            return null;
        }

        return new HidStream(handle, collection.Path, collection.InReportLen, collection.OutReportLen);
    }
}
