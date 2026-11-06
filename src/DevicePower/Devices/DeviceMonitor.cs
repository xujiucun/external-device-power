using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePower.Hid;
using DevicePower.Settings;

namespace DevicePower.Devices;

public sealed class DeviceMonitor
{
    private readonly object _ioLock = new();
    private readonly Dictionary<string, IDeviceConnection> _connections = new();
    private readonly Dictionary<string, DeviceRow> _rows = new();
    private readonly HashSet<string> _knownKeys = new();
    private CancellationTokenSource _cts;
    private CancellationTokenSource _delay;

    public DeviceMonitor(AppSettings settings)
    {
        Settings = settings;
        Look = new DisplayLook(settings);
    }

    public AppSettings Settings { get; }

    /// <summary>外观换算结果，界面直接绑定它。</summary>
    public DisplayLook Look { get; }

    public ObservableCollection<DeviceRow> Rows { get; } = new();

    /// <summary>任务栏电量条用：每列最多两台设备，超出后另起一列。</summary>
    public ObservableCollection<DeviceColumn> Columns { get; } = new();

    /// <summary>任意一台设备读数变化时触发，界面已同步到 UI 线程。</summary>
    public event Action Updated;

    /// <summary>设置保存后触发，用于切换任务栏 / 面板等显示方式。</summary>
    public event Action SettingsChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    public void RequestRefresh()
    {
        try { _delay?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>设置保存后调用：套用设备名称并重新拉一次读数。</summary>
    public void ApplySettings()
    {
        RunOnUi(() =>
        {
            Look.Refresh();
            foreach (DeviceRow row in Rows)
            {
                row.Name = ResolveName(row.Key, row.DefaultName);
                row.ApplyVisuals(Look);
            }

            SyncNames();
        });

        RequestRefresh();
        SettingsChanged?.Invoke();
    }

    /// <summary>向所有设备重新读取一次电量，返回读取成功的台数。</summary>
    public Task<int> ReadNowAsync() => Task.Run(PollAll);

    private async Task LoopAsync(CancellationToken ct)
    {
        bool simulated = false;

        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            PollAll();

            if (!simulated)
            {
                simulated = true;
                AddSimulatedDevices();
            }

            if (ct.IsCancellationRequested) break;

            int intervalMs = Math.Clamp(Settings.PollSeconds, 2, 60) * 1000;
            int wait = (int)(intervalMs - started.Elapsed.TotalMilliseconds);
            if (wait < 0) wait = 0;

            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _delay = delayCts;
            try
            {
                await Task.Delay(wait, delayCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _delay = null;
                delayCts.Dispose();
            }
        }

        lock (_ioLock)
        {
            foreach (IDeviceConnection connection in _connections.Values) connection.Dispose();
            _connections.Clear();
        }
    }

    private int PollAll()
    {
        List<HidCollection> collections;
        try
        {
            collections = HidNative.Enumerate();
        }
        catch
        {
            collections = new List<HidCollection>();
        }

        int reads = 0;
        lock (_ioLock)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HidCollection collection in collections
                         .OrderBy(c => MchoseConnection.Matches(c) ? 0 : 1))
            {
                IDeviceConnection connection = GetConnection(collection);
                if (connection == null) continue;

                seen.Add(collection.Path);

                foreach (DeviceDescriptor device in connection.Discover())
                {
                    EnsureRow(device);

                    BatteryReading? reading = connection.Read(device);
                    if (reading is BatteryReading value)
                    {
                        Publish(device, value);
                        reads++;
                    }
                }
            }

            foreach (string path in _connections.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _connections[path].Dispose();
                _connections.Remove(path);
            }
        }

        return reads;
    }

    private IDeviceConnection GetConnection(HidCollection collection)
    {
        if (_connections.TryGetValue(collection.Path, out IDeviceConnection existing)) return existing;

        IDeviceConnection created = null;
        if (MchoseConnection.Matches(collection))
            created = new MchoseConnection(collection, Settings.DeviceName);
        else if (LogitechConnection.Matches(collection))
            created = new LogitechConnection(collection);

        if (created != null) _connections[collection.Path] = created;
        return created;
    }

    private void EnsureRow(DeviceDescriptor device)
    {
        if (_knownKeys.Contains(device.Key)) return;
        _knownKeys.Add(device.Key);

        RunOnUi(() =>
        {
            if (_rows.ContainsKey(device.Key)) return;

            var row = new DeviceRow(device.Key, device.Family, device.DefaultName,
                ResolveName(device.Key, device.DefaultName));
            row.ApplyVisuals(Look);

            _rows[device.Key] = row;
            Rows.Add(row);
            RebuildColumns();
            Updated?.Invoke();
        });
    }

    private void Publish(DeviceDescriptor device, BatteryReading reading)
    {
        RunOnUi(() =>
        {
            if (!_rows.TryGetValue(device.Key, out DeviceRow row))
            {
                row = new DeviceRow(device.Key, device.Family, device.DefaultName,
                    ResolveName(device.Key, device.DefaultName));
                _rows[device.Key] = row;
                Rows.Add(row);
                RebuildColumns();
            }

            row.IsCharging = reading.IsCharging;
            row.Level = reading.Level;
            row.ApplyVisuals(Look);
            Updated?.Invoke();
        });
    }

    private void RebuildColumns()
    {
        Columns.Clear();
        for (int i = 0; i < Rows.Count; i += 2)
        {
            DeviceRow top = Rows[i];
            DeviceRow bottom = i + 1 < Rows.Count ? Rows[i + 1] : null;
            Columns.Add(new DeviceColumn(top, bottom, i > 0));
        }

        // 名字列宽是按最长名字量出来的，行数变了要重量一次
        SyncNames();
    }

    private void SyncNames() => Look.SetNames(Rows.Select(row => row.Name).ToArray());

    /// <summary>
    /// 演示用假设备：环境变量 DEVICEPOWER_SIM_GPW=电量（末尾加 c 表示充电中）时，
    /// 在第一轮轮询后插入一台模拟的罗技 GPW2，不影响真实设备。
    /// </summary>
    private void AddSimulatedDevices()
    {
        string spec = Environment.GetEnvironmentVariable("DEVICEPOWER_SIM_GPW");
        if (string.IsNullOrWhiteSpace(spec)) return;

        bool charging = spec.EndsWith("c", StringComparison.OrdinalIgnoreCase);
        string digits = charging ? spec[..^1] : spec;
        if (!int.TryParse(digits, out int level)) level = 60;
        level = Math.Clamp(level, 0, 100);

        RunOnUi(() =>
        {
            const string key = "sim:gpw2";
            if (_rows.ContainsKey(key)) return;

            var row = new DeviceRow(key, "logitech", "GPW2", "GPW2");
            _knownKeys.Add(key);
            _rows[key] = row;
            Rows.Add(row);
            row.IsCharging = charging;
            row.Level = level;
            row.ApplyVisuals(Look);
            RebuildColumns();
            Updated?.Invoke();
        });
    }

    private string ResolveName(string key, string defaultName)
    {
        if (Settings.DeviceNames != null &&
            Settings.DeviceNames.TryGetValue(key, out string custom) &&
            !string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        return defaultName;
    }

    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}
