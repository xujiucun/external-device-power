using System.Collections.Generic;

namespace DevicePower.Devices;

/// <summary>任务栏里一列最多放两台设备，上下排列。</summary>
public sealed class DeviceColumn
{
    public DeviceColumn(DeviceRow top, DeviceRow bottom, bool showSeparator)
    {
        Devices = bottom == null ? new[] { top } : new[] { top, bottom };
        ShowSeparator = showSeparator;
    }

    public IReadOnlyList<DeviceRow> Devices { get; }

    public bool ShowSeparator { get; }
}
