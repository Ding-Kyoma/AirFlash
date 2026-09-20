using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AirFlash.Core;
namespace AirFlash.App.Services;
// Windows DNS-SD callbacks are rooted statically; native context is an opaque token, never a GC pointer.
public sealed class WindowsDiscovery : IDiscoveryService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (ServiceRecord Record, DateTime Expires)> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Operation> _browsers = [];
    private System.Threading.Timer? _timer;
    private bool _disposed;
    private long _epoch;
    public event Action<IReadOnlyList<Receiver>>? Changed;
    public event Action<string>? Failed;
    public void Start()
    {
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        Restart();
        _timer = new(_ => Refresh(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }
    private void NetworkChanged(object? sender, EventArgs args) => Restart();
    private void Restart()
    {
        Operation[] old; long epoch;
        lock (_sync)
        {
            if (_disposed) return;
            epoch = ++_epoch;
            old = Operation.All.Values.Where(o => ReferenceEquals(o.Owner, this)).ToArray();
            _browsers.Clear(); _records.Clear(); _instances.Clear(); _pending.Clear();
        }
        // Cancel outside the cache lock: completion callbacks may need that lock.
        foreach (var operation in old) operation.Dispose();
        lock (_sync)
        {
            if (_disposed || epoch != _epoch) return;
            foreach (var type in new[] { "_airplay._tcp.local", "_raop._tcp.local" })
            {
                var operation = new Operation(this, type, type, _epoch, false);
                _browsers.Add(operation);
                var status = operation.Start();
                if (status is not (0 or 9506)) Failed?.Invoke(L.Format("Receiver discovery failed: {0}. You can add a receiver manually.", new Win32Exception((int)status).Message));
            }
        }
        Publish();
    }
    private void Refresh()
    {
        KeyValuePair<string, string>[] instances;
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var key in _records.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) { _records.Remove(key); _instances.Remove(key); }
            instances = _instances.ToArray();
        }
        Publish();
        foreach (var item in instances) Resolve(item.Key, item.Value);
    }
    private void Browse(Operation operation, uint status, IntPtr records)
    {
        try
        {
            if (status != 0) return;
            for (var item = records; item != IntPtr.Zero; item = Marshal.ReadIntPtr(item))
            {
                // DNS_RECORDW x64: next/name pointers, type/data size, flags/TTL/reserved, data union.
                if (Marshal.ReadInt16(item, 16) != 12) continue;
                var instance = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 32));
                if (string.IsNullOrEmpty(instance)) continue;
                lock (_sync)
                {
                    if (_disposed || operation.Epoch != _epoch) return;
                    if (Marshal.ReadInt32(item, 24) == 0) { _records.Remove(instance); _instances.Remove(instance); _pending.Remove(instance); continue; }
                    _instances[instance] = operation.Type;
                }
                Resolve(instance, operation.Type);
            }
            Publish();
        }
        catch (Exception error) { Failed?.Invoke(L.Format("Receiver discovery: {0}", error.Message)); }
        finally { if (records != IntPtr.Zero) DnsRecordListFree(records, 1); }
    }
    private void Resolve(string instance, string type)
    {
        lock (_sync)
        {
            if (_disposed || _pending.ContainsKey(instance)) return;
            var operation = new Operation(this, instance, type, _epoch, true);
            _pending[instance] = operation.Token;
            var status = operation.Start();
            if (status is not (0 or 9506)) { _pending.Remove(instance); operation.Dispose(); }
        }
    }
    private void Resolved(Operation operation, uint status, IntPtr result)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || operation.Epoch != _epoch || !_pending.TryGetValue(operation.Name, out var token) || token != operation.Token) return;
                _pending.Remove(operation.Name);
                if (status != 0 || result == IntPtr.Zero || !_instances.ContainsKey(operation.Name)) return;
                var info = Marshal.PtrToStructure<ServiceInstance>(result);
                if (info.Ipv4 == IntPtr.Zero || info.Port == 0 || info.Count > 256) return;
                var address = new byte[4]; Marshal.Copy(info.Ipv4, address, 0, 4);
                var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < info.Count; i++)
                {
                    var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Keys, i * IntPtr.Size));
                    var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Values, i * IntPtr.Size));
                    if (key is not null) txt[key] = value ?? "";
                }
                _records[operation.Name] = (new(operation.Name, operation.Type, new IPAddress(address).ToString(), info.Port, txt), DateTime.UtcNow.AddSeconds(90));
            }
            Publish();
        }
        catch (Exception error) { Failed?.Invoke(L.Format("Resolving receiver: {0}", error.Message)); }
        finally { if (result != IntPtr.Zero) DnsServiceFreeInstance(result); operation.Complete(); }
    }
    private void Publish()
    {
        IReadOnlyList<Receiver> receivers;
        lock (_sync) { if (_disposed) return; receivers = ReceiverAggregator.Build(_records.Values.Select(p => p.Record)); }
        Changed?.Invoke(receivers);
    }
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged; _timer?.Dispose();
        Operation[] operations;
        lock (_sync)
        {
            if (_disposed) return; _disposed = true; ++_epoch;
            operations = Operation.All.Values.Where(o => ReferenceEquals(o.Owner, this)).ToArray();
            _browsers.Clear();
        }
        foreach (var operation in operations) operation.Dispose();
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ResolveCallback(uint status, IntPtr context, IntPtr instance);
    private static readonly BrowseCallback BrowseThunk = (status, context, records) =>
    {
        if (Operation.All.TryGetValue(context.ToInt64(), out var op)) op.Owner.Browse(op, status, records);
        else if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
    };
    private static readonly ResolveCallback ResolveThunk = (status, context, result) =>
    {
        if (Operation.All.TryGetValue(context.ToInt64(), out var op)) op.Owner.Resolved(op, status, result);
        else if (result != IntPtr.Zero) DnsServiceFreeInstance(result);
    };
    private sealed class Operation : IDisposable
    {
        public static readonly ConcurrentDictionary<long, Operation> All = new();
        private static long _nextToken;
        public long Token { get; } = Interlocked.Increment(ref _nextToken);
        public WindowsDiscovery Owner { get; }
        public string Name { get; }
        public string Type { get; }
        public long Epoch { get; }
        private readonly bool _resolve;
        private IntPtr _name;
        private IntPtr _cancel;
        private bool _pending;
        public Operation(WindowsDiscovery owner, string name, string type, long epoch, bool resolve)
        {
            Owner = owner; Name = name; Type = type; Epoch = epoch; _resolve = resolve;
            _name = Marshal.StringToHGlobalUni(name); _cancel = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(_cancel, IntPtr.Zero); All[Token] = this;
        }
        public uint Start()
        {
            var request = new ServiceRequest { Version = 1, QueryName = _name, Callback = _resolve ? Marshal.GetFunctionPointerForDelegate(ResolveThunk) : Marshal.GetFunctionPointerForDelegate(BrowseThunk), Context = new(Token) };
            var status = _resolve ? DnsServiceResolve(ref request, _cancel) : DnsServiceBrowse(ref request, _cancel);
            _pending = status == 9506;
            return status;
        }
        public void Complete() { _pending = false; Dispose(); }
        public void Dispose()
        {
            if (!All.TryRemove(Token, out _)) return;
            if (_pending) { if (_resolve) DnsServiceResolveCancel(_cancel); else DnsServiceBrowseCancel(_cancel); }
            Marshal.FreeHGlobal(_name); Marshal.FreeHGlobal(_cancel); _name = _cancel = IntPtr.Zero;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceRequest { public uint Version, InterfaceIndex; public IntPtr QueryName, Callback, Context; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceInstance { public IntPtr InstanceName, HostName, Ipv4, Ipv6; public ushort Port, Priority, Weight; public int Count; public IntPtr Keys, Values; public uint InterfaceIndex; }
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowse(ref ServiceRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolve(ref ServiceRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowseCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolveCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int freeType);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
}
