using System.ComponentModel;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

// Scoped discovery uses the same coordinator as deadline/cancellation fixtures.
// Settlement in this fake is an explicit guarantee, never an OS observation.
public sealed class WindowsDiscoveryTests
{
    private const string Type = "_airplay._tcp.local", Name = "Office._airplay._tcp.local";
    private static ServiceRecord Record(string address = "192.0.2.10") => new(Name, Type, address, 7000,
        new Dictionary<string, string> { ["deviceid"] = "synthetic-device" });
    [Fact]
    public void MissingDnsEntryPointsReportsFailureWithoutOpeningQueries()
    {
        var api = new FakeDns(); using var discovery = Create(api, dnsAvailable: () => false);
        string? failure = null; discovery.Failed += value => failure = value.Message;
        discovery.Start(); Assert.NotNull(failure); Assert.Empty(api.Operations);
        Assert.Equal(0, discovery.Snapshot.ActiveQueries);
    }
    [Fact]
    public void CallbackErrorStatusIsReportedWithoutPublishingInvalidResults()
    {
        var api = new FakeDns(); using var discovery = Create(api);
        var failures = 0; discovery.Failed += _ => failures++;
        discovery.Start(); api.Browsers[0].Announce(5);
        api.Browsers[1].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); api.Resolvers[0].Resolve(5, null);
        Assert.Equal(2, failures); Assert.Equal(0, discovery.Snapshot.CachedRecords);
    }
    [Fact]
    public void FailedBrowsersReportPausedAndHiddenInterfaceChangesNeverStartQueries()
    {
        var api = new FakeDns { FailBrowse = true }; using var discovery = Create(api);
        var running = false; var failures = 0;
        discovery.RunningChanged += value => running = value; discovery.Failed += _ => failures++;
        discovery.Start(); Assert.False(running); Assert.Equal(2, failures);
        discovery.Stop(); api.FailBrowse = false; discovery.SetInterface("missing"); Assert.Equal(2, api.Operations.Count);
        discovery.SetInterface(""); discovery.Start(); Assert.Equal(4, api.Browsers.Count); Assert.True(running);
    }
    [Fact]
    public void RepeatedBrowseKeepsOneResolveAndLateRetiredResultsAreRejected()
    {
        var api = new FakeDns(); using var discovery = Create(api);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); discovery.Start(); Assert.Equal(2, api.Browsers.Count);
        for (var i = 0; i < 100; i++) api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120));
        Assert.Single(api.Resolvers);
        api.Resolvers[0].Resolve(0, Record()); api.Resolvers[0].Resolve(0, Record("192.0.2.11"));
        Assert.Equal("192.0.2.10", Assert.Single(last).Address); Assert.Equal(1, api.Resolvers[0].Stops);
        var changes = 0; discovery.Changed += _ => changes++;
        discovery.Stop(); discovery.Stop(); Assert.Equal(0, changes);
        Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
        api.Resolvers[0].Resolve(0, Record("192.0.2.99")); Assert.Equal(0, changes);
        discovery.Start(); api.Browsers[2].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); api.Resolvers[1].Resolve(0, Record()); Assert.Single(last);
    }
    [Fact]
    public void RemovalCancelsOutsideCacheLockAndRejectsLateResults()
    {
        var api = new FakeDns(); using var discovery = Create(api);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); api.Resolvers[0].Resolve(0, Record());
        api.Resolvers[0].DuringStop = () => Assert.True(Task.Run(() => api.Resolvers[0].Resolve(0, Record())).Wait(TimeSpan.FromSeconds(2)), "Cancellation held the cache lock.");
        api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 0)); Assert.Equal(1, api.Resolvers[0].Stops); Assert.Empty(last);
    }
    [Fact]
    public void EarlyCallbackCanStopBeforeStartReturns()
    {
        var api = new FakeDns { EarlyBrowse = true, EarlyResolve = true }; using var discovery = Create(api);
        discovery.Changed += items => { if (items.Count > 0) discovery.Stop(); };
        discovery.Start(); Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops)); Assert.Equal(0, discovery.Snapshot.ActiveQueries);
    }
    [Fact]
    public void FailedQueryRetriesAfterBackoffAndSwitchingInterfaceCancelsOldEpoch()
    {
        var time = new ManualTime(); var api = new FakeDns { FailResolve = true };
        using var discovery = Create(api, time, () => [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)]);
        var failures = 0; discovery.Failed += _ => failures++;
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); Assert.Equal(1, failures);
        api.FailResolve = false; api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); Assert.Single(api.Resolvers);
        time.Advance(TimeSpan.FromSeconds(1)); discovery.Tick(); Assert.Equal(2, api.Resolvers.Count);
        var old = api.Operations.ToArray(); discovery.SetInterface("nic"); Assert.All(old, operation => Assert.True(operation.IsSettled));
        api.Resolvers[0].Resolve(0, Record()); Assert.Empty(last);
        Assert.All(api.Browsers.Skip(2), operation => Assert.Equal(17u, operation.Index));
        discovery.Dispose(); Assert.All(api.Operations, operation => Assert.True(operation.IsSettled));
    }
    [Fact]
    public void TtlExpiresOnlyWhileScanningAndResumedCacheGetsGracePeriod()
    {
        var time = new ManualTime(); var api = new FakeDns(); using var discovery = Create(api, time);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); api.Resolvers[0].Resolve(0, Record());
        discovery.Stop(); time.Advance(TimeSpan.FromMinutes(5)); discovery.Tick(); Assert.Single(last);
        discovery.Start(); time.Advance(TimeSpan.FromSeconds(9)); discovery.Tick(); Assert.Single(last);
        time.Advance(TimeSpan.FromSeconds(1)); discovery.Tick(); Assert.Empty(last);
        api.Browsers[2].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 30)); api.Resolvers[1].Resolve(0, Record());
        time.Advance(TimeSpan.FromSeconds(30)); discovery.Tick(); Assert.Empty(last); Assert.Equal(1, api.Resolvers[1].Stops);
    }
    [Fact]
    public void InterfaceAvailabilityDescribesOnlyTheCurrentLookupWhileScanning()
    {
        var api = new FakeDns(); var time = new ManualTime();
        var lookupSnapshots = new List<bool?>();
        var failLookup = false;
        WindowsDiscovery? observing = null;
        using var discovery = Create(api, time, adapters: () =>
        {
            lookupSnapshots.Add(observing!.SnapshotDiagnostics().InterfaceAvailable);
            if (failLookup) throw new System.Net.NetworkInformation.NetworkInformationException();
            return [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)];
        });
        observing = discovery;
        bool? Availability() => discovery.SnapshotDiagnostics().InterfaceAvailable;
        var initialAvailability = Availability();

        discovery.SetInterface("nic");
        discovery.Start();
        Assert.True(Availability());
        Assert.Equal(2, api.Browsers.Count);
        time.Advance(TimeSpan.FromSeconds(20)); discovery.Tick();
        Assert.True(Availability());
        Assert.Null(lookupSnapshots[^1]);
        Assert.Equal(2, api.Browsers.Count);

        discovery.Stop();
        Assert.Null(Availability());
        Assert.Null(initialAvailability);
        using var bundle = System.Text.Json.JsonDocument.Parse(DiagnosticExport.Compose("test", "test", null,
            discovery: discovery.SnapshotDiagnostics()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            bundle.RootElement.GetProperty("discovery").GetProperty("interface_available").ValueKind);
        discovery.SetInterface("missing");
        Assert.Null(Availability());
        Assert.Equal(2, lookupSnapshots.Count);

        discovery.Start();
        Assert.False(Availability());
        Assert.Equal(2, api.Browsers.Count);

        var publishedAvailability = new List<bool?>();
        discovery.Changed += _ => publishedAvailability.Add(discovery.SnapshotDiagnostics().InterfaceAvailable);
        discovery.SetInterface("nic");
        Assert.NotEmpty(publishedAvailability);
        Assert.Null(publishedAvailability[0]);
        Assert.True(Availability());
        Assert.Equal(4, api.Browsers.Count);

        failLookup = true;
        discovery.SetInterface("missing");
        Assert.Null(Availability());
        Assert.Equal(4, api.Browsers.Count);
        Assert.Equal(5, lookupSnapshots.Count);

        failLookup = false;
        time.Advance(TimeSpan.FromSeconds(20)); discovery.Tick();
        Assert.False(Availability());
        Assert.Equal(4, api.Browsers.Count);
        Assert.Equal(6, lookupSnapshots.Count);
        Assert.All(lookupSnapshots, observation => Assert.Null(observation));
    }

    [Fact]
    public void PtrAfterRestartExtendsCacheWithoutClaimingAResolvedEndpointOrIdentity()
    {
        var time = new ManualTime(); var api = new FakeDns(); using var discovery = Create(api, time);
        discovery.Start();
        api.Browsers[0].Announce(0, new DnsAnnouncement(Name, 120));
        api.Resolvers[0].Resolve(0, Record() with
        {
            Txt = new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:01", ["tsid"] = "synthetic-stereo" }
        });
        discovery.Stop();
        discovery.Start();
        time.Advance(TimeSpan.FromSeconds(9)); discovery.Tick();
        var before = discovery.SnapshotDiagnostics();
        var resolved = Assert.Single(before.History, entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded);
        Assert.NotNull(resolved.DeviceToken);
        Assert.NotNull(resolved.StereoToken);
        Assert.NotNull(resolved.EndpointToken);
        Assert.Equal(1, before.CachedRecords);

        api.Browsers[2].Announce(0, new DnsAnnouncement(Name, 30));

        var after = discovery.SnapshotDiagnostics();
        var events = after.History.Where(entry => entry.Sequence > before.RecordedEvents).ToArray();
        Assert.Collection(events,
            entry => Assert.Equal(DiscoveryTraceEvent.AdvertisementAdded, entry.Event),
            entry =>
            {
                Assert.Equal("CachedRecordTtlRefreshed", entry.Event.ToString());
                Assert.Equal(DiscoveryServiceType.Airplay, entry.ServiceType);
                Assert.Equal(resolved.ServiceToken, entry.ServiceToken);
                Assert.Equal(30u, entry.TtlSeconds);
                Assert.Null(entry.DeviceToken);
                Assert.Null(entry.StereoToken);
                Assert.Null(entry.EndpointToken);
                Assert.Null(entry.StereoMetadataPresent);
            });
        Assert.Equal(2, api.Resolvers.Count);
        Assert.Equal(1, after.PendingResolves);

        time.Advance(TimeSpan.FromSeconds(1)); discovery.Tick();
        Assert.Equal(1, discovery.SnapshotDiagnostics().CachedRecords);
        time.Advance(TimeSpan.FromSeconds(29)); discovery.Tick();
        Assert.Equal(0, discovery.SnapshotDiagnostics().CachedRecords);
    }

    [Fact]
    public void RepeatedFakeSessionsSettleWithTheExplicitFakeBoundaryGuarantee()
    {
        var api = new FakeDns(); using var discovery = Create(api);
        for (var i = 0; i < 100; i++)
        {
            discovery.Start(); api.Browsers[^2].Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); api.Resolvers[^1].Resolve(0, Record()); discovery.Stop();
            Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops)); Assert.Equal(0, discovery.Snapshot.NativeOwners);
        }
    }
    private static WindowsDiscovery Create(FakeDns api, ManualTime? time = null, Func<IReadOnlyList<DiscoveryAdapter>>? adapters = null, Func<bool>? dnsAvailable = null)
        => new(api, time ?? new(), adapters ?? (() => []), _ => { }, dnsAvailable: dnsAvailable);
    private sealed class FakeDns : IDnsServiceApi
    {
        public List<Operation> Operations { get; } = [];
        public List<Operation> Browsers => Operations.Where(operation => !operation.IsResolve).ToList();
        public List<Operation> Resolvers => Operations.Where(operation => operation.IsResolve).ToList();
        public bool EarlyBrowse, EarlyResolve, FailResolve, FailBrowse;
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback) => Create(type, index, callback, null);
        public IDnsServiceOperation CreateResolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback) => Create(instance, index, null, callback);
        private Operation Create(string name, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve)
        {
            var operation = new Operation(this, Operations.Count + 1, name, index, browse, resolve); Operations.Add(operation); return operation;
        }
    }
    private sealed class Operation(FakeDns owner, long token, string name, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve) : IDnsServiceOperation
    {
        public long Token => token;
        public string Name => name;
        public uint Index => index;
        public bool IsResolve => resolve is not null;
        public bool IsSettled { get; private set; }
        public int Stops { get; private set; }
        public Action? DuringStop;
        public event Action? Settled;
        public event Action<string>? CancellationFailed { add { } remove { } }
        public event Action<string>? CallbackFailed { add { } remove { } }
        public uint Start()
        {
            if (IsResolve ? owner.FailResolve : owner.FailBrowse) { Settle(); throw new Win32Exception(87); }
            if (IsResolve && owner.EarlyResolve) Resolve(0, Record());
            if (!IsResolve && owner.EarlyBrowse) Announce(0, new DnsAnnouncement(WindowsDiscoveryTests.Name, 120)); return 9506;
        }
        public uint Cancel() { if (IsSettled) return 0; Stops++; DuringStop?.Invoke(); Settle(); return 0; }
        public void Announce(uint status, params DnsAnnouncement[] values) => browse!(status, values);
        public void Resolve(uint status, ServiceRecord? value) => resolve!(status, value);
        private void Settle() { if (IsSettled) return; IsSettled = true; Settled?.Invoke(); }
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan span) => _timestamp += span.Ticks;
    }
}
