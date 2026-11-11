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

    /// <summary>本轮读到电量的设备；只有读到过才算“在场”，接收器拔掉就从界面移除。</summary>
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>休眠中的设备：接收器还在，但这轮没应答，界面保留上次读数并压暗。</summary>
    private readonly HashSet<string> _asleep = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>_present 的快照：轮询线程会改集合，界面只读快照。</summary>
    private volatile HashSet<string> _presentSnapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 有线直连的罗技设备在线时，接收器里的同一只鼠标通常已经不应答（休眠）。
    /// 这时把接收器那行藏起来，避免同一只鼠标同时显示“有线”和“接收器”两条。
    /// </summary>
    private volatile HashSet<string> _hiddenByDirect = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>整个进程只推一次上线通知：休眠唤醒、读数抖动都不再算新的连接。</summary>
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>发现过的全部设备（含被隐藏的）。设置页的显示开关绑它，Rows 只放显示中的。</summary>
    public ObservableCollection<DeviceRow> Catalog { get; } = new();

    /// <summary>任务栏电量条用：每列最多两台设备，超出后另起一列。</summary>
    public ObservableCollection<DeviceColumn> Columns { get; } = new();

    /// <summary>任意一台设备读数变化时触发，界面已同步到 UI 线程。</summary>
    public event Action Updated;

    /// <summary>设备从读不到恢复为读到电量（上线）时触发，界面已同步到 UI 线程。</summary>
    public event Action<DeviceRow> DeviceOnline;

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
        HashSet<string> present = _presentSnapshot;

        RunOnUi(() =>
        {
            Look.Refresh();
            foreach (DeviceRow row in Catalog)
            {
                row.Name = ResolveName(row.Key, row.DefaultName);
                row.ApplyVisuals(Look);
            }

            SyncVisible(present);
        });

        RequestRefresh();
        SettingsChanged?.Invoke();
    }

    /// <summary>向所有设备重新读取一次电量，返回读取成功的台数。</summary>
    public Task<int> ReadNowAsync() => Task.Run(PollAll);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            PollAll();

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
        var cameOnline = new List<string>();
        var wentOffline = new List<string>();
        var goneKeys = new List<string>();
        var asleepNow = new List<string>();
        bool presenceChanged = false;
        bool stateChanged = false;
        bool hiddenChanged = false;
        HashSet<string> presentSnapshot;
        lock (_ioLock)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HidCollection collection in collections
                         .OrderBy(c => MchoseConnection.Matches(c) ? 0 : 1))
            {
                IDeviceConnection connection = GetConnection(collection);
                if (connection == null) continue;

                seen.Add(collection.Path);

                foreach (DeviceDescriptor device in connection.Discover())
                {
                    EnsureRow(device);
                    seenKeys.Add(device.Key);

                    // 隐藏的设备只登记进 Catalog 供设置页勾选，不读也不参与“在场”判定
                    if (IsHidden(device.Key))
                    {
                        // 记成已通知：以后重新勾选时不该被当成一次“上线”再推
                        _announced.Add(device.Key);
                        if (_asleep.Remove(device.Key)) stateChanged = true;
                        if (_present.Remove(device.Key))
                        {
                            wentOffline.Add(device.Key);
                            presenceChanged = true;
                        }
                        continue;
                    }

                    BatteryReading? reading = connection.Read(device);
                    if (reading is BatteryReading value)
                    {
                        if (_present.Add(device.Key)) presenceChanged = true;
                        if (_asleep.Remove(device.Key)) stateChanged = true;
                        Publish(device, value);
                        reads++;

                        // 每台设备整个进程只推一次；中途读不到再恢复不算新的连接
                        if (_announced.Add(device.Key)) cameOnline.Add(device.Key);
                    }
                    else if (_present.Contains(device.Key))
                    {
                        // 设备还在（接收器没拔），只是这轮没回应：保留上次读数并标记休眠
                        if (_asleep.Add(device.Key))
                        {
                            asleepNow.Add(device.Key);
                            stateChanged = true;
                        }
                    }
                    else if (_present.Remove(device.Key))
                    {
                        wentOffline.Add(device.Key);
                        presenceChanged = true;
                    }
                }
            }

            foreach (string path in _connections.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _connections[path].Dispose();
                _connections.Remove(path);
            }

            // 接收器整个被拔掉时这台设备本轮根本没出现，也要按离线处理
            foreach (string key in _present.Where(k => !seenKeys.Contains(k)).ToList())
            {
                _present.Remove(key);
                if (_asleep.Remove(key)) stateChanged = true;
                wentOffline.Add(key);
                presenceChanged = true;
            }

            // 本轮完全没发现的设备（接收器拔掉、或数据线直连的设备已拔出）：
            // 从设置列表和内部映射里移除；下次插回来会重新发现，名称仍从配置恢复。
            foreach (string key in _knownKeys.Where(k => !seenKeys.Contains(k)).ToList())
            {
                _knownKeys.Remove(key);
                goneKeys.Add(key);
                presenceChanged = true;
            }

            presentSnapshot = new HashSet<string>(_present, StringComparer.OrdinalIgnoreCase);
            _presentSnapshot = presentSnapshot;

            // 直连（数据线）的罗技设备在线时，接收器里对应的那条多半已经休眠：藏起来去重
            bool hasDirectLogitech = false;
            foreach (string key in _present)
            {
                if (key.StartsWith("logi:", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith("#ff", StringComparison.OrdinalIgnoreCase))
                {
                    hasDirectLogitech = true;
                    break;
                }
            }

            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hasDirectLogitech)
            {
                foreach (string key in _asleep)
                {
                    if (key.StartsWith("logi:", StringComparison.OrdinalIgnoreCase) &&
                        !key.EndsWith("#ff", StringComparison.OrdinalIgnoreCase))
                        hidden.Add(key);
                }
            }

            hiddenChanged = !_hiddenByDirect.SetEquals(hidden);
            _hiddenByDirect = hidden;
        }

        if (presenceChanged || stateChanged || hiddenChanged)
        {
            RunOnUi(() =>
            {
                if (asleepNow.Count > 0)
                {
                    foreach (string key in asleepNow)
                        if (_rows.TryGetValue(key, out DeviceRow row))
                            row.IsAsleep = true;
                }

                if (wentOffline.Count > 0)
                {
                    // 接收器拔掉/隐藏：读数和充电状态归零，再从电量条 / 面板移除
                    foreach (string key in wentOffline)
                    {
                        ClearReading(key);
                        DiagLog.WriteIfChanged("offline", $"{key} 已离线，从界面移除");
                    }
                }

                if (goneKeys.Count > 0)
                {
                    // 设备已不在本机：从设置页的设备列表里也移除，避免残留旧设备
                    foreach (string key in goneKeys)
                    {
                        if (!_rows.TryGetValue(key, out DeviceRow row)) continue;
                        _rows.Remove(key);
                        Catalog.Remove(row);
                        DiagLog.WriteIfChanged("offline", $"{key} 已拔出，从设置列表移除");
                    }
                }

                SyncVisible(presentSnapshot);

                // 只有本次进程里第一次读到电量时才推，隐藏设备的 key 早已记进 _announced
                foreach (string key in cameOnline)
                    if (_rows.TryGetValue(key, out DeviceRow row))
                        DeviceOnline?.Invoke(row);
            });
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
        else if (StandardBatteryConnection.Matches(collection))
            created = new StandardBatteryConnection(collection);

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
            Catalog.Add(row);
            Updated?.Invoke();
        });
    }

    private void Publish(DeviceDescriptor device, BatteryReading reading)
    {
        RunOnUi(() =>
        {
            if (!_rows.TryGetValue(device.Key, out DeviceRow row)) return;

            row.IsAsleep = false;
            row.IsCharging = reading.IsCharging;
            row.Level = reading.Level;
            row.ApplyVisuals(Look);
            Updated?.Invoke();
        });
    }

    private bool IsHidden(string key) =>
        Settings.HiddenDevices != null &&
        Settings.HiddenDevices.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>读不到电量时把行清干净，避免低电量颜色、充电标记留在界面上。</summary>
    private void ClearReading(string key)
    {
        if (!_rows.TryGetValue(key, out DeviceRow row)) return;
        row.Level = -1;
        row.IsCharging = false;
    }

    /// <summary>
    /// 按 Catalog 顺序重排显示中的行：只有在场且没被隐藏的设备才进 Rows，
    /// 界面、低电量提醒、托盘图标都看不到离线设备。
    /// </summary>
    private void SyncVisible(HashSet<string> present)
    {
        Rows.Clear();
        foreach (DeviceRow row in Catalog)
            if (present.Contains(row.Key) && !IsHidden(row.Key) && !_hiddenByDirect.Contains(row.Key))
                Rows.Add(row);

        RebuildColumns();
        Updated?.Invoke();
    }

    private void RebuildColumns()
    {
        Columns.Clear();

        // 满组（两台）要贴着任务栏那一侧：靠托盘时满组放右边，落单的一台挪到最左边；
        // 靠开始按钮时满组天然就在左边。4 台/6 台都是整组，不存在这个问题。
        bool partialFirst = Rows.Count % 2 == 1 && Settings.TaskbarSide != "left";
        if (partialFirst)
        {
            Columns.Add(new DeviceColumn(Rows[Rows.Count - 1], null, false));
            for (int i = 0; i < Rows.Count - 1; i += 2)
                Columns.Add(new DeviceColumn(Rows[i], Rows[i + 1], true));
        }
        else
        {
            for (int i = 0; i < Rows.Count; i += 2)
            {
                DeviceRow top = Rows[i];
                DeviceRow bottom = i + 1 < Rows.Count ? Rows[i + 1] : null;
                Columns.Add(new DeviceColumn(top, bottom, i > 0));
            }
        }

        // 名字列宽是按最长名字量出来的，行数变了要重量一次
        SyncNames();
    }

    private void SyncNames() => Look.SetNames(Rows.Select(row => row.Name).ToArray());

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
