using System.ComponentModel;
using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class WindowsDiscoveryTests
{
    private const string Type = "_airplay._tcp.local", Name = "Office._airplay._tcp.local";
    private static ServiceRecord Record(string address = "192.0.2.10") => new(Name, Type, address, 7000, new Dictionary<string, string> { ["deviceid"] = "aabbcc" });
    [Fact]
    public void MissingDnsEntryPointsReportsFailureWithoutOpeningQueries()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [], dnsAvailable: () => false);
        string? failure = null; discovery.Failed += value => failure = value;
        discovery.Start(); Assert.NotNull(failure); Assert.Empty(api.Operations);
    }
    [Fact]
    public void CallbackErrorStatusIsReportedAndDoesNotDiscardShellState()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        var failures = 0; discovery.Failed += _ => failures++;
        discovery.Start(); api.Browsers[0].Callback(5, []);
        api.Browsers[0].Callback(0, [new(Name, 120)]);
        api.Resolvers[0].Callback(5, null);
        Assert.Equal(2, failures);
    }
    [Fact]
    public void FailedBrowsersReportPausedAndHiddenInterfaceChangesNeverStartQueries()
    {
        var api = new FakeDns { FailBrowse = true }; using var discovery = new WindowsDiscovery(api, () => []);
        var running = true; var failures = 0;
        discovery.RunningChanged += value => running = value; discovery.Failed += _ => failures++;
        discovery.Start(); Assert.False(running); Assert.Equal(2, failures);
        discovery.Stop(); api.FailBrowse = false; discovery.SetInterface("missing");
        Assert.Empty(api.Operations);
        discovery.SetInterface(""); discovery.Start(); Assert.Equal(2, api.Browsers.Count);
    }
    [Fact]
    public void RepeatedBrowseAndResultsKeepOneResolverUntilStop()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); discovery.Start(); Assert.Equal(2, api.Browsers.Count);
        for (var i = 0; i < 100; i++) api.Browsers[0].Callback(0, [new(Name, 120)]);
        Assert.Single(api.Resolvers);
        api.Resolvers[0].Callback(0, Record()); api.Resolvers[0].Callback(0, Record("192.0.2.11"));
        Assert.Equal("192.0.2.11", Assert.Single(last).Address);
        Assert.Equal(0, api.Resolvers[0].Stops);
        var changes = 0; discovery.Changed += _ => changes++;
        discovery.Stop(); discovery.Stop(); Assert.Equal(0, changes);
        Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
        api.Resolvers[0].Callback(0, Record("192.0.2.99")); Assert.Equal(0, changes);
        discovery.Start(); Assert.Equal(4, api.Browsers.Count);
        api.Browsers[2].Callback(0, [new(Name, 120)]); api.Resolvers[1].Callback(0, Record());
        Assert.Single(last);
    }
    [Fact]
    public void RemovalCancelsOutsideCacheLockAndRejectsLateResults()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        api.Resolvers[0].DuringStop = () =>
        {
            var callback = Task.Run(() => api.Resolvers[0].Callback(0, Record()));
            Assert.True(callback.Wait(TimeSpan.FromSeconds(2)), "Cancellation held the cache lock.");
        };
        api.Browsers[0].Callback(0, [new(Name, 0)]);
        Assert.Equal(1, api.Resolvers[0].Stops); Assert.Empty(last);
    }
    [Fact]
    public void EarlyCallbackCanStopBeforeHandleIsAttached()
    {
        var api = new FakeDns { EarlyBrowse = true, EarlyResolve = true };
        using var discovery = new WindowsDiscovery(api, () => []);
        discovery.Changed += items => { if (items.Count > 0) discovery.Stop(); };
        discovery.Start(); Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
    }
    [Fact]
    public void FailedQueryCanRetryAndSwitchingInterfaceCancelsOldEpoch()
    {
        var api = new FakeDns { FailResolve = true }; using var discovery = new WindowsDiscovery(api, () => [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)]);
        var failures = 0; discovery.Failed += _ => failures++;
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); Assert.Equal(1, failures);
        api.FailResolve = false; api.Browsers[0].Callback(0, [new(Name, 120)]); Assert.Single(api.Resolvers);
        var old = api.Operations.ToArray();
        discovery.SetInterface("nic"); Assert.All(old, operation => Assert.Equal(1, operation.Stops));
        api.Resolvers[0].Callback(0, Record()); Assert.Empty(last);
        Assert.All(api.Browsers.Skip(2), operation => Assert.Equal(17u, operation.Index));
        discovery.Dispose(); Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
    }
    [Fact]
    public void TtlExpiresOnlyWhileScanningAndResumedCacheGetsGracePeriod()
    {
        var time = new ManualTime(); var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [], time: time);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        discovery.Stop(); time.Advance(TimeSpan.FromMinutes(5)); Assert.Single(last);
        discovery.Start(); time.Advance(TimeSpan.FromSeconds(9)); Assert.Single(last);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Empty(last);
        api.Browsers[2].Callback(0, [new(Name, 30)]); api.Resolvers[1].Callback(0, Record());
        time.Advance(TimeSpan.FromSeconds(30)); Assert.Empty(last); Assert.Equal(1, api.Resolvers[1].Stops);
    }
    [Fact]
    public void RepeatedSessionsLeaveNoActiveOperations()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        for (var i = 0; i < 100; i++)
        {
            discovery.Start(); api.Browsers[^2].Callback(0, [new(Name, 120)]); api.Resolvers[^1].Callback(0, Record()); discovery.Stop();
            Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
        }
    }
    [Fact]
    public void NativeResultDoesNotReleaseCancellationHandleAndEveryResultIsFreed()
    {
        var native = new FakeNative(); var callbacks = 0;
        var operation = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => callbacks++, _ => { });
        operation.Start(); native.Emit(); native.Emit(); Assert.Equal(2, callbacks); Assert.Equal(2, native.Frees); Assert.False(operation.IsReleased);
        native.CallbackOnCancel = true; operation.Dispose(); operation.Dispose();
        Assert.Equal(1, native.Cancels); Assert.Equal(3, native.Frees); Assert.Equal(2, callbacks); Assert.True(operation.IsReleased);
    }
    [Fact]
    public void NativeEarlyCallbackDefersCancellationUntilStartReturns()
    {
        var native = new FakeNative { CallbackOnStart = true }; WindowsDnsSdApi.Operation? operation = null;
        operation = new(native, Name, 0, true, (_, _) => operation!.Dispose(), _ => { });
        operation.Start(); Assert.Equal(1, native.Cancels); Assert.True(operation.IsReleased); Assert.Equal(1, native.Frees);
    }
    [Fact]
    public void NativeStartFailureAndCancellationRetryRetainCorrectOwnership()
    {
        var native = new FakeNative { StartStatus = 87 };
        var failed = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => { }, _ => { });
        Assert.Throws<Win32Exception>(failed.Start); Assert.True(failed.IsReleased); Assert.Equal(0, native.Cancels);
        native.StartStatus = 9506; native.CancelStatus = 5;
        var operation = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => { }, _ => { });
        operation.Start(); Assert.Throws<Win32Exception>(operation.Dispose); Assert.False(operation.IsReleased);
        native.CancelStatus = 0; operation.Dispose(); Assert.True(operation.IsReleased); Assert.Equal(2, native.Cancels);
    }
    private sealed class FakeDns : IDnsSdApi
    {
        public List<BrowseOperation> Browsers { get; } = [];
        public List<ResolveOperation> Resolvers { get; } = [];
        public IEnumerable<FakeOperation> Operations => Browsers.Cast<FakeOperation>().Concat(Resolvers);
        public bool EarlyBrowse, EarlyResolve, FailResolve, FailBrowse;
        public IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed)
        {
            if (FailBrowse) throw new Win32Exception(87);
            var operation = new BrowseOperation(index, callback); Browsers.Add(operation);
            if (EarlyBrowse) callback(0, [new(Name, 120)]); return operation;
        }
        public IDisposable Resolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed)
        {
            if (FailResolve) throw new Win32Exception(87);
            var operation = new ResolveOperation(index, callback); Resolvers.Add(operation);
            if (EarlyResolve) callback(0, Record()); return operation;
        }
    }
    private class FakeOperation(uint index) : IDisposable
    {
        public uint Index { get; } = index;
        public int Stops { get; private set; }
        public Action? DuringStop;
        public void Dispose() { Stops++; DuringStop?.Invoke(); }
    }
    private sealed class BrowseOperation(uint index, Action<uint, IReadOnlyList<DnsSdService>> callback) : FakeOperation(index) { public Action<uint, IReadOnlyList<DnsSdService>> Callback { get; } = callback; }
    private sealed class ResolveOperation(uint index, Action<uint, ServiceRecord?> callback) : FakeOperation(index) { public Action<uint, ServiceRecord?> Callback { get; } = callback; }
    private sealed class FakeNative : IWindowsDnsApi
    {
        private DnsSdRequest _request;
        public int Cancels, Frees;
        public uint StartStatus = 9506, CancelStatus;
        public bool CallbackOnStart, CallbackOnCancel;
        public uint Start(ref DnsSdRequest request, IntPtr cancel, bool resolve)
        {
            _request = request; Marshal.WriteIntPtr(cancel, new(123)); if (CallbackOnStart) Emit(); return StartStatus;
        }
        public uint Cancel(IntPtr cancel, bool resolve)
        {
            Assert.Equal(new IntPtr(123), Marshal.ReadIntPtr(cancel)); Cancels++;
            if (CallbackOnCancel) Assert.True(Task.Run(Emit).Wait(TimeSpan.FromSeconds(2)), "Cancellation held the operation lock.");
            return CancelStatus;
        }
        public void Emit() => Marshal.GetDelegateForFunctionPointer<DnsSdCallback>(_request.Callback)(0, _request.Context, new(1));
        public IReadOnlyList<DnsSdService> ReadBrowse(IntPtr records) => [new(Name, 120)];
        public ServiceRecord? ReadResolve(string name, string type, IntPtr result) => Record();
        public void FreeResult(IntPtr result, bool resolve) => Frees++;
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private ManualTimer? _timer;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _timer = new(this, callback, state);
        public void Advance(TimeSpan span) { _now += span; _timer?.Fire(); }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime; return true; }
            public void Fire() { if (_due <= owner.GetUtcNow()) { _due = null; callback(state); } }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
