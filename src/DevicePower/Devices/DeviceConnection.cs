using DevicePower.Hid;

namespace DevicePower.Devices;

internal readonly record struct BatteryReading(int Level, bool IsCharging);

/// <summary>一台可独立显示电量的设备。Key 在接收器重新插拔后保持不变。</summary>
internal sealed class DeviceDescriptor
{
    public DeviceDescriptor(string key, string family, string defaultName, byte slot = 0)
    {
        Key = key;
        Family = family;
        DefaultName = defaultName;
        Slot = slot;
    }

    public string Key { get; }
    public string Family { get; }
    public string DefaultName { get; }

    /// <summary>罗技接收器内的设备序号；其他协议为 0。</summary>
    public byte Slot { get; }
}

internal interface IDeviceConnection : System.IDisposable
{
    /// <summary>列出这个 HID 接口后面的设备；设备休眠时可能暂时为空。</summary>
    System.Collections.Generic.IReadOnlyList<DeviceDescriptor> Discover();

    BatteryReading? Read(DeviceDescriptor device);
}

internal abstract class HidDeviceConnection : IDeviceConnection
{
    private HidStream _stream;

    protected HidDeviceConnection(HidCollection collection) => Collection = collection;

    protected HidCollection Collection { get; }

    protected HidStream Stream
    {
        get
        {
            if (_stream == null) _stream = HidNative.Open(Collection);
            return _stream;
        }
    }

    protected void DropStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public abstract System.Collections.Generic.IReadOnlyList<DeviceDescriptor> Discover();

    public abstract BatteryReading? Read(DeviceDescriptor device);

    public void Dispose() => DropStream();
}
