using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using AirFlash.Core;

namespace AirFlash.App.Services;

internal interface INativeDnsCalls
{
    uint StartResolve(IntPtr request, IntPtr cancel);
    uint StartBrowse(IntPtr request, IntPtr cancel);
    uint CancelResolve(IntPtr cancel);
    uint CancelBrowse(IntPtr cancel);
    void FreeInstance(IntPtr instance);
    void FreeRecords(IntPtr records);
}

internal sealed class NativeDnsServiceApi(INativeDnsCalls? calls = null) : IDnsServiceApi
{
    internal const int OwnerLimit = 128;
    private const uint RequestPending = 9506, Cancelled = 1223;
    private static readonly object Admission = new();
    private static readonly ConcurrentDictionary<long, Operation> Owners = new();
    private static long _allocatedBuffers, _releasedOwners;
    [ThreadStatic] private static int _dispatchDepth;
    private readonly INativeDnsCalls _calls = calls ?? WindowsCalls.Instance;

    internal static int CurrentOwnerCount => Owners.Count;
    internal static long AllocatedBufferCount => Interlocked.Read(ref _allocatedBuffers);
    internal static long ReleasedOwnerCount => Interlocked.Read(ref _releasedOwners);
    internal static int RetainedResolveOwnerCount => Owners.Values.Count(owner => owner.IsResolve && owner.IsRetired);

    // Fixture teardown only: the fake boundary independently joins every worker
    // before calling this. Windows DNS exposes no corresponding resolve signal.
    // This is never a production settlement path or proof of OS quiescence.
    internal static void SettleQuiescentTestBoundary(INativeDnsCalls calls)
    {
        if (calls is WindowsCalls) throw new InvalidOperationException("A real Windows DNS boundary cannot be settled by a fixture.");
        foreach (var owner in Owners.Values.Where(owner => ReferenceEquals(owner.Calls, calls)))
            owner.SettleQuiescentTestBoundary();
    }
    internal static Task PendingCancellationWorkers(INativeDnsCalls calls)
        => Task.WhenAll(Owners.Values.Where(owner => ReferenceEquals(owner.Calls, calls)).Select(owner => owner.CancellationWorker));

    public IDnsServiceOperation CreateBrowse(string type, uint interfaceIndex, Action<uint, IReadOnlyList<DnsAnnouncement>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Create(type, type, interfaceIndex, callback, null);
    }

    public IDnsServiceOperation CreateResolve(string name, string type, uint interfaceIndex, Action<uint, ServiceRecord?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Create(name, type, interfaceIndex, null, callback);
    }

    private Operation Create(string name, string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Windows DNS-SD discovery requires a 64-bit process.");
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Admission)
        {
            // Retained owners from every instance count, including previous discovery epochs.
            if (Owners.Count >= OwnerLimit) throw new DnsServiceCapacityException();
            var token = DnsServiceOwnership.Acquire();
            try
            {
                var operation = new Operation(token, name, type, index, _calls, browse, resolve);
                Owners[operation.Token] = operation;
                return operation;
            }
            catch { DnsServiceOwnership.Release(); throw; }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ResolveCallback(uint status, IntPtr context, IntPtr instance);
    private static readonly BrowseCallback BrowseThunk = (status, context, records) => DispatchBrowse(status, context, records);
    private static readonly ResolveCallback ResolveThunk = (status, context, instance) => DispatchResolve(status, context, instance);

    internal static void DispatchBrowse(uint status, IntPtr context, IntPtr records, INativeDnsCalls? fallbackCalls = null)
    {
        ++_dispatchDepth;
        try
        {
            if (Owners.TryGetValue(context.ToInt64(), out var owner) && !owner.IsResolve) owner.Browse(status, records);
            else if (records != IntPtr.Zero) (fallbackCalls ?? WindowsCalls.Instance).FreeRecords(records);
        }
        catch (Exception error) { TraceFailure("DNS-SD browse thunk", error); }
        finally { --_dispatchDepth; }
    }

    internal static void DispatchResolve(uint status, IntPtr context, IntPtr instance, INativeDnsCalls? fallbackCalls = null)
    {
        ++_dispatchDepth;
        try
        {
            if (Owners.TryGetValue(context.ToInt64(), out var owner) && owner.IsResolve) owner.Resolve(status, instance);
            else if (instance != IntPtr.Zero) (fallbackCalls ?? WindowsCalls.Instance).FreeInstance(instance);
        }
        catch (Exception error) { TraceFailure("DNS-SD resolve thunk", error); }
        finally { --_dispatchDepth; }
    }

    private sealed class Operation : IDnsServiceOperation
    {
        private readonly object _gate = new();
        private readonly INativeDnsCalls _calls;
        private readonly string _type;
        private Action<uint, IReadOnlyList<DnsAnnouncement>>? _browse;
        private Action<uint, ServiceRecord?>? _resolve;
        private IntPtr _name, _request, _cancel;
        private bool _startInvoked, _startReturned, _cancelRequested, _cancelIssued, _cancelScheduled, _terminal, _released;
        private int _activeCalls, _activeCallbacks;
        private Task _cancellationWorker = Task.CompletedTask;
        private uint _startStatus = RequestPending, _cancelStatus;
        public long Token { get; }
        public string Name { get; }
        public bool IsResolve { get; }
        internal INativeDnsCalls Calls => _calls;
        internal bool IsRetired { get { lock (_gate) return _cancelRequested || _terminal; } }
        internal Task CancellationWorker { get { lock (_gate) return _cancellationWorker; } }
        public bool IsSettled { get { lock (_gate) return _released; } }
        public event Action? Settled;
        public event Action<string>? CancellationFailed;
        public event Action<string>? CallbackFailed;

        public Operation(long token, string name, string type, uint index, INativeDnsCalls calls,
            Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve)
        {
            Token = token; Name = name; _type = type; _calls = calls; _browse = browse; _resolve = resolve; IsResolve = resolve is not null;
            try
            {
                _name = Marshal.StringToHGlobalUni(name); Interlocked.Increment(ref _allocatedBuffers);
                _request = Allocate(Marshal.SizeOf<NativeServiceRequest>());
                _cancel = Allocate(IntPtr.Size); Marshal.WriteIntPtr(_cancel, IntPtr.Zero);
                Marshal.StructureToPtr(new NativeServiceRequest
                {
                    Version = 1, InterfaceIndex = index, QueryName = _name, Context = new(Token),
                    Callback = IsResolve ? Marshal.GetFunctionPointerForDelegate(ResolveThunk) : Marshal.GetFunctionPointerForDelegate(BrowseThunk)
                }, _request, false);
            }
            catch { FreeInputs(); throw; }
        }

        public uint Start()
        {
            lock (_gate)
            {
                if (_released || _cancelRequested && !_startInvoked) return Cancelled;
                if (_startInvoked) return _startStatus;
                _startInvoked = true; ++_activeCalls;
            }
            uint status = 0;
            bool returned = false, neverEntered = false;
            try
            {
                status = IsResolve ? _calls.StartResolve(_request, _cancel) : _calls.StartBrowse(_request, _cancel);
                returned = true;
                return status;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                neverEntered = true;
                throw;
            }
            finally
            {
                Action? settled; bool cancel;
                lock (_gate)
                {
                    _startReturned = true; _startStatus = status; --_activeCalls;
                    // DNS-SD documents pending as its only successful start result.
                    // Loader failures never entered native code. An ambiguous injected
                    // boundary exception is retained rather than assumed unaccepted.
                    if (neverEntered || returned && status != RequestPending) _terminal = true;
                    cancel = _cancelRequested && !_terminal && !_cancelIssued && !_cancelScheduled;
                    settled = ReleaseIfSafe();
                }
                Notify(settled);
                if (cancel) Cancel();
            }
        }

        public uint Cancel()
        {
            Action? settled = null;
            bool callNative = false, queue = false;
            lock (_gate)
            {
                _cancelRequested = true; _browse = null; _resolve = null;
                if (_released) return 0;
                if (!_startInvoked) { _terminal = true; settled = ReleaseIfSafe(); }
                // The native cancel handle is written by Start. An inline callback may
                // request cancellation before that call returns: queue, never wait for it.
                else if (!_startReturned || _terminal || _cancelIssued) return _cancelStatus;
                // Cancellation ordering/reentrancy is undocumented. The callback
                // must never wait for a cancel call that could join that same callback,
                // including callback A retiring a different operation B.
                else if (_activeCallbacks != 0 || _dispatchDepth != 0 || _cancelScheduled)
                    queue = ScheduleIfReady();
                else { _cancelIssued = true; ++_activeCalls; callNative = true; }
            }
            Notify(settled);
            if (queue) QueueCancellation();
            if (!callNative) return 0;
            return InvokeCancellation(deferred: false);
        }

        private uint InvokeCancellation(bool deferred)
        {
            uint status = 0;
            bool returned = false;
            Exception? failure = null;
            try
            {
                status = IsResolve ? _calls.CancelResolve(_cancel) : _calls.CancelBrowse(_cancel);
                returned = true;
                return status;
            }
            catch (Exception error)
            {
                failure = error;
                if (!deferred) throw;
                return 0;
            }
            finally
            {
                Action? settled;
                Action<string>? failed = null;
                string? message = null;
                lock (_gate)
                {
                    if (returned) _cancelStatus = status;
                    --_activeCalls;
                    if (deferred && (failure is not null || returned && status != 0))
                    {
                        failed = CancellationFailed;
                        message = failure?.Message ?? new Win32Exception(unchecked((int)status)).Message;
                    }
                    settled = ReleaseIfSafe();
                }
                Notify(settled);
                NotifyFailure(failed, message);
            }
        }

        // Must be called under _gate. The callback exit or Start return supplies
        // the missing eligibility transition; no worker waits on either frame.
        private bool ScheduleIfReady()
        {
            if (_released || _terminal || !_cancelRequested || !_startReturned ||
                _activeCallbacks != 0 || _cancelIssued || _cancelScheduled) return false;
            _cancelScheduled = true;
            return true;
        }

        private void QueueCancellation()
        {
            try
            {
                lock (_gate) _cancellationWorker = Task.Run(RunQueuedCancellation);
            }
            catch (Exception error)
            {
                Action<string>? failed;
                lock (_gate) { _cancelScheduled = false; failed = CancellationFailed; }
                NotifyFailure(failed, error.Message);
            }
        }

        private void RunQueuedCancellation()
        {
            lock (_gate)
            {
                _cancelScheduled = false;
                if (_released || _terminal || !_startReturned || _activeCallbacks != 0 || _cancelIssued) return;
                _cancelIssued = true; ++_activeCalls;
            }
            InvokeCancellation(deferred: true);
        }

        private bool EnterCallback(bool terminal)
        {
            lock (_gate)
            {
                if (_released) return false;
                ++_activeCallbacks;
                if (terminal) _terminal = true;
                return true;
            }
        }

        public void Resolve(uint status, IntPtr instance)
        {
            // Neither a result nor an error status proves query completion. The
            // resolve API has no documented final cancellation notification.
            bool entered = EnterCallback(false);
            try
            {
                if (!entered) return;
                Action<uint, ServiceRecord?>? callback;
                lock (_gate) callback = _resolve;
                if (callback is not null) callback(status, status == 0 && instance != IntPtr.Zero ? ReadInstance(instance) : null);
            }
            catch (Exception error) { NotifyFailure(CallbackFailed, error.Message); }
            finally
            {
                try { if (instance != IntPtr.Zero) _calls.FreeInstance(instance); }
                catch (Exception error) { NotifyFailure(CallbackFailed, error.Message); }
                finally { if (entered) ExitCallback(); }
            }
        }

        public void Browse(uint status, IntPtr records)
        {
            // DnsServiceBrowseCancel promises a final ERROR_CANCELLED (1223)
            // callback. Other errors, including ERROR_OPERATION_ABORTED (995),
            // do not prove that the asynchronous browse is finished.
            bool entered = EnterCallback(status == Cancelled);
            try
            {
                if (!entered) return;
                Action<uint, IReadOnlyList<DnsAnnouncement>>? callback;
                lock (_gate) callback = _browse;
                if (callback is not null) callback(status, status == 0 ? ReadRecords(records) : Array.Empty<DnsAnnouncement>());
            }
            catch (Exception error) { NotifyFailure(CallbackFailed, error.Message); }
            finally
            {
                try { if (records != IntPtr.Zero) _calls.FreeRecords(records); }
                catch (Exception error) { NotifyFailure(CallbackFailed, error.Message); }
                finally { if (entered) ExitCallback(); }
            }
        }

        private ServiceRecord? ReadInstance(IntPtr instance)
        {
            var info = Marshal.PtrToStructure<NativeServiceInstance>(instance);
            if (info.Ipv4 == IntPtr.Zero || info.Port == 0 || info.Count > 256 ||
                info.Count != 0 && (info.Keys == IntPtr.Zero || info.Values == IntPtr.Zero)) return null;
            var address = new byte[4]; Marshal.Copy(info.Ipv4, address, 0, address.Length);
            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < info.Count; ++i)
            {
                var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Keys, i * IntPtr.Size));
                var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Values, i * IntPtr.Size));
                if (key is not null) txt[key] = value ?? "";
            }
            return new(Name, _type, new IPAddress(address).ToString(), info.Port, txt);
        }

        private static IReadOnlyList<DnsAnnouncement> ReadRecords(IntPtr records)
        {
            var result = new List<DnsAnnouncement>();
            var visited = new HashSet<IntPtr>();
            for (var item = records; item != IntPtr.Zero && visited.Count < 1024 && visited.Add(item); item = Marshal.ReadIntPtr(item))
            {
                if ((ushort)Marshal.ReadInt16(item, 16) != 12) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 32));
                // DNS_RECORDW: flags at20, dwTtl at24, PTR union pointer at32 (x64).
                if (!string.IsNullOrEmpty(name)) result.Add(new(name, unchecked((uint)Marshal.ReadInt32(item, 24))));
            }
            return result;
        }

        private void ExitCallback()
        {
            Action? settled; bool queue;
            lock (_gate)
            {
                --_activeCallbacks; settled = ReleaseIfSafe(); queue = ScheduleIfReady();
            }
            Notify(settled);
            // ExitCallback is still inside the reverse P/Invoke. Always use a
            // worker here, even though the managed callback frame is finished.
            if (queue) QueueCancellation();
        }

        private Action? ReleaseIfSafe()
        {
            if (_released || !_terminal || _activeCalls != 0 || _activeCallbacks != 0) return null;
            _released = true; _browse = null; _resolve = null;
            FreeInputs(); Owners.TryRemove(Token, out _); DnsServiceOwnership.Release(); Interlocked.Increment(ref _releasedOwners);
            var handlers = Settled; Settled = null; CancellationFailed = null; CallbackFailed = null;
            return handlers;
        }

        internal void SettleQuiescentTestBoundary()
        {
            Action? settled;
            lock (_gate)
            {
                if (_activeCalls != 0 || _activeCallbacks != 0 || _cancelScheduled)
                    throw new InvalidOperationException("The fake DNS boundary still has an active or queued frame.");
                _terminal = true; settled = ReleaseIfSafe();
            }
            Notify(settled);
        }

        private void FreeInputs() { Free(ref _name); Free(ref _request); Free(ref _cancel); }
        private static IntPtr Allocate(int size)
        {
            var result = Marshal.AllocHGlobal(size); Interlocked.Increment(ref _allocatedBuffers); return result;
        }
        private static void Free(ref IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(pointer); pointer = IntPtr.Zero; Interlocked.Decrement(ref _allocatedBuffers);
        }
        private static void Notify(Action? settled)
        {
            if (settled is null) return;
            foreach (Action callback in settled.GetInvocationList())
                try { callback(); }
                catch (Exception error) { TraceFailure("DNS-SD settlement callback", error); }
        }
        private static void NotifyFailure(Action<string>? failed, string? message)
        {
            if (failed is null || message is null) return;
            foreach (Action<string> callback in failed.GetInvocationList())
                try { callback(message); }
                catch (Exception error) { TraceFailure("DNS-SD failure observer", error); }
        }
    }

    private static void TraceFailure(string context, Exception error)
    {
        try { System.Diagnostics.Trace.TraceError("{0}: {1}", context, DiagnosticText.Protect(error.Message)); }
        catch { } // Even a custom Trace listener must not escape a reverse P/Invoke.
    }

    // DNS-SD pages do not promise callback quiescence when Cancel returns. Retain
    // all three input buffers after successful Cancel. Browse has a documented
    // final1223 callback; pending resolve has no documented quiescence signal,
    // so its retired owner remains bounded and rooted until process exit.
    // https://learn.microsoft.com/windows/win32/api/windns/nf-windns-dnsserviceresolvecancel
    // https://learn.microsoft.com/windows/win32/api/windns/nf-windns-dnsservicebrowsecancel
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeServiceRequest
    {
        public uint Version, InterfaceIndex;
        public IntPtr QueryName, Callback, Context;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeServiceInstance
    {
        public IntPtr InstanceName, HostName, Ipv4, Ipv6;
        public ushort Port, Priority, Weight;
        public uint Count;
        public IntPtr Keys, Values;
        public uint InterfaceIndex;
    }

    private sealed class WindowsCalls : INativeDnsCalls
    {
        public static readonly WindowsCalls Instance = new();
        public uint StartResolve(IntPtr request, IntPtr cancel) => unchecked((uint)DnsServiceResolve(request, cancel));
        public uint StartBrowse(IntPtr request, IntPtr cancel) => unchecked((uint)DnsServiceBrowse(request, cancel));
        public uint CancelResolve(IntPtr cancel) => unchecked((uint)DnsServiceResolveCancel(cancel));
        public uint CancelBrowse(IntPtr cancel) => unchecked((uint)DnsServiceBrowseCancel(cancel));
        public void FreeInstance(IntPtr instance) => DnsServiceFreeInstance(instance);
        public void FreeRecords(IntPtr records) => DnsRecordListFree(records, 1);
        [DllImport("dnsapi.dll")] private static extern int DnsServiceBrowse(IntPtr request, IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern int DnsServiceResolve(IntPtr request, IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern int DnsServiceBrowseCancel(IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern int DnsServiceResolveCancel(IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int freeType);
        [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
    }
}
