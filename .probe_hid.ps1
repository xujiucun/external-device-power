$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public class HidEnum2 {
  [StructLayout(LayoutKind.Explicit)]
  struct SP_DEVICE_INTERFACE_DATA {
    [FieldOffset(0)] public int cbSize;
    [FieldOffset(8)] public Guid InterfaceClassGuid;
    [FieldOffset(24)] public int Flags;
    [FieldOffset(32)] public IntPtr Reserved;
  }
  [DllImport("setupapi.dll", SetLastError=true)]
  static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, int f);
  [DllImport("setupapi.dll", SetLastError=true)]
  static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA data);
  [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, ref int required, IntPtr devInfo);

  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);
  [DllImport("hid.dll")] static extern bool HidD_GetProductString(IntPtr h, IntPtr b, uint len);
  [DllImport("hid.dll")] static extern bool HidD_GetManufacturerString(IntPtr h, IntPtr b, uint len);
  [DllImport("hid.dll")] static extern bool HidD_GetSerialNumberString(IntPtr h, IntPtr b, uint len);
  [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_GetAttributes(IntPtr h, out ATTR a);
  [StructLayout(LayoutKind.Explicit, Size=16)] public struct ATTR { [FieldOffset(0)] public long Size; [FieldOffset(8)] public short Vid; [FieldOffset(10)] public short Pid; [FieldOffset(12)] public short Ver; }

  public static List<string> Run() {
    Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000015");
    var list = new List<string>();
    IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
    if (set.ToInt64() == -1) { list.Add("SetupDiGetClassDevs failed err=" + Marshal.GetLastWin32Error()); return list; }
    for (int i = 0; ; i++) {
      var d = new SP_DEVICE_INTERFACE_DATA();
      d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
      if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref d)) { list.Add("ENUM-STOP i=" + i + " err=" + Marshal.GetLastWin32Error() + " sizeof(SPDID)=" + d.cbSize); break; }
      int req = 0;
      SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, ref req, IntPtr.Zero);
      if (req <= 0) continue;
      IntPtr buf = Marshal.AllocHGlobal(req);
      Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4);
      string path = null;
      if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, ref req, IntPtr.Zero))
        path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
      Marshal.FreeHGlobal(buf);
      if (path == null) continue;
      IntPtr h = CreateFileW(path, 0xC0000000u | 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
      if (h == IntPtr.Zero || h.ToInt64() == -1) {
        list.Add(path + "\n   OPEN-FAILED err=" + Marshal.GetLastWin32Error());
        continue;
      }
      ATTR a; a.Size = Marshal.SizeOf(typeof(ATTR)); a.Vid = 0; a.Pid = 0; a.Ver = 0;
      HidD_GetAttributes(h, out a);
      string p = Str(h, 0), m = Str(h, 1), s = Str(h, 2);
      list.Add(string.Format("{0}\n   VID={1:X4} PID={2:X4} VER={3:X4}  PRODUCT='{4}'  MFG='{5}'  SERIAL='{6}'", path, (ushort)a.Vid, (ushort)a.Pid, (ushort)a.Ver, p, m, s));
      CloseHandle(h);
    }
    return list;
  }
  static string Str(IntPtr h, int which) {
    IntPtr b = Marshal.AllocHGlobal(600);
    try {
      bool ok = which == 0 ? HidD_GetProductString(h, b, 600) : (which == 1 ? HidD_GetManufacturerString(h, b, 600) : HidD_GetSerialNumberString(h, b, 600));
      return ok ? Marshal.PtrToStringUni(b) : "(null)";
    } finally { Marshal.FreeHGlobal(b); }
  }
}
'@
Add-Type -Language CSharp -TypeDefinition $src
$out = 'F:\MyProject\external-device-power\.probe_hid.txt'
$res = [HidEnum2]::Run()
$L = New-Object System.Collections.Generic.List[string]
$L.Add("=== All present HID device interfaces, with iProduct / iManufacturer / iSerial ===")
$res | ForEach-Object { $L.Add($_) }
$L.Add("")
$L.Add("=== filtered: 05AC ===")
$res | Where-Object { $_ -match '05ac|05AC' } | ForEach-Object { $L.Add($_) }
$L | Out-File -FilePath $out -Encoding utf8
"written: $out  count=" + $res.Count
