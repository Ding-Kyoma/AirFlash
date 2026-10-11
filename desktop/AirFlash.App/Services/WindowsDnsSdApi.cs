using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using AirFlash.Core;
namespace AirFlash.App.Services;

internal sealed record DnsSdService(string Instance, uint Ttl);
internal interface IDnsSdApi
{
    IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed);
    IDisposable Resolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed);
}

// Result notifications do not end native queries. Retain cancellation buffers
// until explicit cancellation returns, including when callbacks arrive early.
internal sealed class WindowsDnsSdApi(IWindowsDnsApi? native = null) : IDnsSdApi
{
    private readonly IWindowsDnsApi _native = native ?? new WindowsDnsApi();
    public IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed)
        => Start(type, index, false, (status, result) => callback(status, result == IntPtr.Zero ? [] : _native.ReadBrowse(result)), failed);
    public IDisposable Resolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed)
        => Start(instance, index, true, (status, result) => callback(status, result == IntPtr.Zero ? null : _native.ReadResolve(instance, type, result)), failed);
    private IDisposable Start(string name, uint index, bool resolve, Action<uint, IntPtr> callback, Action<string> failed)
    {
        var operation = new Operation(_native, name, index, resolve, callback, failed);
        operation.Start(); return operation;
    }
    internal sealed class Operation : IDisposable
    {
        private static readonly ConcurrentDictionary<long, Operation> All = new();
        internal static int ActiveCount => All.Count;
        internal bool IsReleased { get { lock (_sync) return _released; } }
        private static long _nextToken;
        private static readonly DnsSdCallback BrowseThunk = (status, context, result) => Deliver(status, context, result, false);
        private static readonly DnsSdCallback ResolveThunk = (status, context, result) => Deliver(status, context, result, true);
        private readonly object _sync = new();
        private readonly IWindowsDnsApi _native;
        private readonly Action<uint, IntPtr> _callback;
        private readonly Action<string> _failed;
        private readonly bool _resolve;
        private readonly uint _index;
        private readonly long _token = Interlocked.Increment(ref _nextToken);
        private IntPtr _name, _cancel;
        private bool _starting, _pending, _stopping, _cancelling, _released;
        internal Operation(IWindowsDnsApi native, string name, uint index, bool resolve, Action<uint, IntPtr> callback, Action<string> failed)
        {
            _native = native; _index = index; _resolve = resolve; _callback = callback; _failed = failed;
            _name = Marshal.StringToHGlobalUni(name); _cancel = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_cancel, IntPtr.Zero); All[_token] = this;
        }
        internal void Start()
        {
            lock (_sync) { if (_stopping) return; _starting = true; }
            uint status;
            try
            {
                var request = new DnsSdRequest { Version = 1, InterfaceIndex = _index, QueryName = _name,
                    Callback = Marshal.GetFunctionPointerForDelegate(_resolve ? ResolveThunk : BrowseThunk), Context = new(_token) };
                status = _native.Start(ref request, _cancel, _resolve);
            }
            catch { lock (_sync) _starting = false; Dispose(); throw; }
            bool stop;
            lock (_sync) { _starting = false; _pending = status == 9506; stop = _stopping; }
            if (status is not (0 or 9506)) { Dispose(); throw new Win32Exception((int)status); }
            if (stop) Dispose();
        }
        private static void Deliver(uint status, IntPtr context, IntPtr result, bool resolve)
        {
            // Nothing may escape a managed thunk into the native DNS callback thread.
            try
            {
                if (All.TryGetValue(context.ToInt64(), out var operation)) operation.Deliver(status, result);
                else if (result != IntPtr.Zero) new WindowsDnsApi().FreeResult(result, resolve);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError(DiagnosticText.Protect($"DNS-SD callback failed: {error}")); }
        }
        private void Deliver(uint status, IntPtr result)
        {
            try { lock (_sync) { if (_stopping) return; } _callback(status, result); }
            catch (Exception error) { NotifyFailure(error); }
            finally
            {
                try { if (result != IntPtr.Zero) _native.FreeResult(result, _resolve); }
                catch (Exception error) { NotifyFailure(error); }
            }
        }
        private void NotifyFailure(Exception error)
        {
            try { _failed(error.Message); }
            catch (Exception notification) { System.Diagnostics.Trace.TraceError(DiagnosticText.Protect($"DNS-SD failure notification failed: {notification}")); }
        }
        public void Dispose()
        {
            bool pending;
            lock (_sync)
            {
                _stopping = true;
                if (_starting || _released || _cancelling) return;
                _cancelling = true; pending = _pending;
            }
            // Native cancellation can call back: never hold the operation lock.
            uint status;
            try { status = pending ? _native.Cancel(_cancel, _resolve) : 0; }
            catch { lock (_sync) _cancelling = false; throw; }
            lock (_sync)
            {
                _cancelling = false;
                if (status == 0)
                {
                    _released = true; All.TryRemove(_token, out _);
                    Marshal.FreeHGlobal(_name); Marshal.FreeHGlobal(_cancel); _name = _cancel = IntPtr.Zero;
                }
            }
            // A failed cancellation must retain buffers for a later retry.
            if (status != 0) throw new Win32Exception((int)status);
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DnsSdCallback(uint status, IntPtr context, IntPtr result);
[StructLayout(LayoutKind.Sequential)] internal struct DnsSdRequest { public uint Version, InterfaceIndex; public IntPtr QueryName, Callback, Context; }
internal interface IWindowsDnsApi
{
    uint Start(ref DnsSdRequest request, IntPtr cancel, bool resolve);
    uint Cancel(IntPtr cancel, bool resolve);
    IReadOnlyList<DnsSdService> ReadBrowse(IntPtr records);
    ServiceRecord? ReadResolve(string name, string type, IntPtr result);
    void FreeResult(IntPtr result, bool resolve);
}
internal sealed class WindowsDnsApi : IWindowsDnsApi
{
    public uint Start(ref DnsSdRequest request, IntPtr cancel, bool resolve) => resolve ? DnsServiceResolve(ref request, cancel) : DnsServiceBrowse(ref request, cancel);
    public uint Cancel(IntPtr cancel, bool resolve) => resolve ? DnsServiceResolveCancel(cancel) : DnsServiceBrowseCancel(cancel);
    public void FreeResult(IntPtr result, bool resolve) { if (resolve) DnsServiceFreeInstance(result); else DnsRecordListFree(result, 1); }
    public IReadOnlyList<DnsSdService> ReadBrowse(IntPtr records)
    {
        var services = new List<DnsSdService>();
        for (var item = records; item != IntPtr.Zero; item = Marshal.ReadIntPtr(item))
        {
            // DNS_RECORDW x64: next/name, type/size, flags/TTL/reserved, data union.
            if (Marshal.ReadInt16(item, 16) != 12) continue;
            var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 32));
            if (!string.IsNullOrEmpty(name)) services.Add(new(name, unchecked((uint)Marshal.ReadInt32(item, 24))));
        }
        return services;
    }
    public ServiceRecord? ReadResolve(string name, string type, IntPtr result)
    {
        var info = Marshal.PtrToStructure<ServiceInstance>(result);
        if (info.Ipv4 == IntPtr.Zero || info.Port == 0 || info.Count is < 0 or > 256) return null;
        var address = new byte[4]; Marshal.Copy(info.Ipv4, address, 0, 4);
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < info.Count; i++)
        {
            var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Keys, i * IntPtr.Size));
            var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Values, i * IntPtr.Size));
            if (key is not null) txt[key] = value ?? "";
        }
        return new(name, type, new IPAddress(address).ToString(), info.Port, txt);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceInstance { public IntPtr InstanceName, HostName, Ipv4, Ipv6; public ushort Port, Priority, Weight; public int Count; public IntPtr Keys, Values; public uint InterfaceIndex; }
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowse(ref DnsSdRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolve(ref DnsSdRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowseCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolveCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int freeType);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
}
