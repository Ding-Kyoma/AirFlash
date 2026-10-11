using System.Net.NetworkInformation;
using System.ComponentModel;
using AirFlash.Core;
namespace AirFlash.App.Services;

public sealed class WindowsDiscovery : IDiscoveryService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ServiceRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _expires = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OperationSlot> _resolvers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OperationSlot> _browsers = [];
    private readonly HashSet<OperationSlot> _retired = [];
    private readonly Func<IReadOnlyList<DiscoveryAdapter>> _adapters;
    private readonly IDnsSdApi _api;
    private readonly Func<bool> _dnsAvailable;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly ITimer _expiry;
    private string _interfaceId = "";
    private uint? _interfaceIndex;
    private bool _started, _disposed;
    private long _epoch;
    private HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    public event Action<IReadOnlyList<Receiver>>? Changed;
    public event Action<string>? Failed;
    public event Action<bool>? RunningChanged;
    public WindowsDiscovery(Func<IReadOnlyList<DiscoveryAdapter>>? adapters = null, Action<string>? log = null, Func<bool>? dnsAvailable = null)
        : this(new WindowsDnsSdApi(), adapters, log, dnsAvailable: dnsAvailable ?? (() => WindowsCapabilities.Current.DnsSdAvailable)) { }
    internal WindowsDiscovery(IDnsSdApi api, Func<IReadOnlyList<DiscoveryAdapter>>? adapters = null, Action<string>? log = null, TimeProvider? time = null, Func<bool>? dnsAvailable = null)
    {
        _api = api; _dnsAvailable = dnsAvailable ?? (() => true); _adapters = adapters ?? NetworkAdapterCatalog.List; _log = log ?? (_ => { }); _time = time ?? TimeProvider.System;
        _expiry = _time.CreateTimer(_ => RunSafely(Expire), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public void SetInterface(string id)
    {
        bool restart;
        lock (_sync)
        {
            if (_disposed || _interfaceId == id) return;
            _interfaceId = id; _records.Clear(); _expires.Clear(); restart = _started;
        }
        Publish();
        if (restart) RunSafely(Restart);
    }
    public void Start()
    {
        if (!_dnsAvailable())
        {
            Failed?.Invoke(L.Get("Windows DNS-SD is unavailable. You can add a receiver manually and collect startup diagnostics."));
            RunningChanged?.Invoke(false); return;
        }
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        }
        RunSafely(Restart);
    }
    public void Stop()
    {
        OperationSlot[] old;
        lock (_sync)
        {
            if (!_started) return;
            _started = false; ++_epoch;
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            _expiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            old = DetachOperations();
        }
        Cancel(old);
        RunningChanged?.Invoke(false);
        // Preserve cache when suspended; don't publish an empty discovery event.
    }
    private void NetworkChanged(object? sender, EventArgs args) => RunSafely(Restart);
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => RunSafely(Restart);
    private void RunSafely(Action action)
    {
        try { action(); }
        catch (Exception error) { _log($"Discovery operation failed: {error}"); Failed?.Invoke(L.Format("Receiver discovery: {0}", error.Message)); }
    }
    private void Restart()
    {
        string selected;
        lock (_sync) { if (_disposed || !_started) return; selected = _interfaceId; }
        uint? index;
        try { index = DiscoveryInterface.ResolveIndex(selected, _adapters()); }
        catch (NetworkInformationException error) { Failed?.Invoke(error.Message); index = null; }
        OperationSlot[] old; long epoch;
        lock (_sync)
        {
            if (_disposed || !_started || selected != _interfaceId) return;
            epoch = ++_epoch; _interfaceIndex = index;
            old = DetachOperations();
            // Show cache immediately, then retire entries not confirmed on resume.
            _expires.Clear();
            foreach (var name in _records.Keys) _expires[name] = _time.GetUtcNow().AddSeconds(10);
            ScheduleExpiry();
        }
        Cancel(old);
        if (index is null)
        {
            RunningChanged?.Invoke(false);
            Failed?.Invoke(L.Get("The selected network interface is unavailable. Discovery is paused."));
            return;
        }
        var browsing = false;
        foreach (var type in new[] { "_airplay._tcp.local", "_raop._tcp.local" })
        {
            var slot = new OperationSlot();
            lock (_sync) { if (!Current(epoch)) return; _browsers.Add(slot); }
            try { slot.Attach(_api.Browse(type, index.Value, (status, records) => RunSafely(() => Browse(epoch, type, status, records)), message => Failed?.Invoke(message))); browsing = true; }
            catch (Exception error) { Cancel([slot]); Failed?.Invoke(L.Format("Receiver discovery failed: {0}. You can add a receiver manually.", error.Message)); }
        }
        lock (_sync) { if (!Current(epoch)) return; }
        RunningChanged?.Invoke(browsing);
    }
    private bool Current(long epoch) => !_disposed && _started && epoch == _epoch;
    private void Browse(long epoch, string type, uint status, IReadOnlyList<DnsSdService> services)
    {
        if (status != 0)
        {
            lock (_sync) { if (!Current(epoch)) return; }
            Failed?.Invoke(L.Format("Receiver discovery: {0}", new Win32Exception((int)status).Message)); return;
        }
        var removed = new List<OperationSlot>();
        var start = new List<(string Name, OperationSlot Slot, uint Index)>();
        lock (_sync)
        {
            if (!Current(epoch)) return;
            foreach (var service in services)
            {
                if (service.Ttl == 0)
                {
                    _records.Remove(service.Instance); _expires.Remove(service.Instance);
                    if (_resolvers.Remove(service.Instance, out var resolver)) removed.Add(resolver);
                }
                else
                {
                    _expires[service.Instance] = _time.GetUtcNow().AddSeconds(Math.Min(service.Ttl, 86400u));
                    if (!_resolvers.ContainsKey(service.Instance))
                    {
                        var slot = new OperationSlot(); _resolvers[service.Instance] = slot;
                        start.Add((service.Instance, slot, _interfaceIndex!.Value));
                    }
                }
            }
            ScheduleExpiry();
        }
        Cancel(removed);
        foreach (var item in start)
        {
            lock (_sync) { if (!Current(epoch) || _resolvers.GetValueOrDefault(item.Name) != item.Slot) continue; }
            try { item.Slot.Attach(_api.Resolve(item.Name, type, item.Index, (code, record) => Resolved(epoch, item.Name, item.Slot, code, record), message => Failed?.Invoke(message))); }
            catch (Exception error)
            {
                lock (_sync) { if (_resolvers.GetValueOrDefault(item.Name) == item.Slot) _resolvers.Remove(item.Name); }
                Cancel([item.Slot]); Failed?.Invoke(L.Format("Resolving receiver: {0}", error.Message));
            }
        }
        Publish();
    }
    private void Resolved(long epoch, string name, OperationSlot slot, uint status, ServiceRecord? record)
    {
        if (status != 0)
        {
            lock (_sync) { if (!Current(epoch) || _resolvers.GetValueOrDefault(name) != slot) return; }
            Failed?.Invoke(L.Format("Resolving receiver: {0}", new Win32Exception((int)status).Message)); return;
        }
        if (record is null) return;
        lock (_sync)
        {
            if (!Current(epoch) || _resolvers.GetValueOrDefault(name) != slot) return;
            if (_records.TryGetValue(name, out var previous) && Equal(previous, record)) return;
            _log("Discovery resolved: " + Describe(record)); _records[name] = record;
        }
        Publish();
    }
    private void ScheduleExpiry()
    {
        var due = _expires.Count == 0 ? Timeout.InfiniteTimeSpan : _expires.Values.Min() - _time.GetUtcNow();
        _expiry.Change(due == Timeout.InfiniteTimeSpan ? due : due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }
    private void Expire()
    {
        var old = new List<OperationSlot>(); bool changed = false;
        lock (_sync)
        {
            if (_disposed || !_started) return;
            foreach (var name in _expires.Where(p => p.Value <= _time.GetUtcNow()).Select(p => p.Key).ToArray())
            {
                _expires.Remove(name); changed |= _records.Remove(name);
                if (_resolvers.Remove(name, out var resolver)) old.Add(resolver);
            }
            ScheduleExpiry();
        }
        Cancel(old); if (changed) Publish();
    }
    private OperationSlot[] DetachOperations()
    {
        var old = _browsers.Concat(_resolvers.Values).Concat(_retired).ToArray();
        _browsers.Clear(); _resolvers.Clear(); _retired.Clear(); return old;
    }
    private void Cancel(IEnumerable<OperationSlot> operations)
    {
        foreach (var operation in operations)
        {
            try { operation.Dispose(); }
            catch (Exception error) { lock (_sync) _retired.Add(operation); Failed?.Invoke(error.Message); }
        }
    }
    private void Publish()
    {
        IReadOnlyList<Receiver> receivers;
        lock (_sync)
        {
            if (_disposed) return;
            var diagnostics = new HashSet<string>(StringComparer.Ordinal);
            receivers = ReceiverAggregator.Build(_records.Values, message => diagnostics.Add(message));
            foreach (var message in diagnostics.Except(_diagnostics)) _log(message);
            _diagnostics = diagnostics;
        }
        Changed?.Invoke(receivers);
    }
    private static bool Equal(ServiceRecord left, ServiceRecord right) => left.Instance == right.Instance && left.ServiceType == right.ServiceType && left.Address == right.Address && left.Port == right.Port &&
        left.Txt.Count == right.Txt.Count && left.Txt.All(p => right.Txt.TryGetValue(p.Key, out var value) && value == p.Value);
    private static string Describe(ServiceRecord record) => System.Text.Json.JsonSerializer.Serialize(new { record.Instance, record.ServiceType, record.Address, record.Port,
        DeviceId = record.Txt.GetValueOrDefault("deviceid", ""), Model = record.Txt.GetValueOrDefault("model", record.Txt.GetValueOrDefault("am", "")), StereoId = record.Txt.GetValueOrDefault("tsid", "") });
    public void Dispose()
    {
        Stop();
        OperationSlot[] old;
        lock (_sync) { if (_disposed) return; _disposed = true; ++_epoch; old = DetachOperations(); }
        Cancel(old); _expiry.Dispose();
    }
    // An early callback can stop discovery before the native handle is returned.
    private sealed class OperationSlot : IDisposable
    {
        private readonly object _sync = new();
        private IDisposable? _operation;
        private bool _stopped;
        public void Attach(IDisposable operation)
        {
            bool stop; lock (_sync) { _operation = operation; stop = _stopped; }
            if (stop) Dispose();
        }
        public void Dispose()
        {
            IDisposable? operation;
            lock (_sync) { _stopped = true; operation = _operation; _operation = null; }
            try { operation?.Dispose(); }
            catch { lock (_sync) _operation = operation; throw; }
        }
    }
}
