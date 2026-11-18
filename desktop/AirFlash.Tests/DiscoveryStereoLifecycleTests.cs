using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

// These are complete fake DNS timelines, not live network or device observations.
public sealed class DiscoveryStereoLifecycleTests
{
    private const string Airplay = "_airplay._tcp.local", Raop = "_raop._tcp.local";
    private const string LeftId = "aabbccddee01", RightId = "aabbccddee02", StereoId = "fixture-stereo";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumedStereoCacheUsesOnlyRenewedServiceEvidenceAfterGrace(bool renewAirplay)
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair();
        var complete = CompletePair(fixture.Catalog);
        var originalTokens = fixture.Discovery.SnapshotDiagnostics().History
            .Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded).Select(entry => entry.ServiceToken).ToArray();
        var originalRecords = fixture.Discovery.SnapshotDiagnostics().History.Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded).ToArray();
        Assert.Equal(originalRecords[0].DeviceToken, originalRecords[1].DeviceToken);
        Assert.Equal(originalRecords[2].DeviceToken, originalRecords[3].DeviceToken);
        Assert.NotNull(originalRecords[0].DeviceToken);
        Assert.NotNull(originalRecords[2].DeviceToken);
        Assert.NotEqual(originalRecords[0].DeviceToken, originalRecords[2].DeviceToken);
        fixture.Discovery.Stop();
        fixture.TickAfter(TimeSpan.FromMinutes(5));
        Assert.Same(complete, Assert.Single(fixture.Catalog));
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
        Assert.Equal(0, fixture.Discovery.Snapshot.ActiveQueries);

        fixture.Discovery.Start();
        fixture.Publish(renewAirplay ? AirplayRecord(right: true) : RaopRecord(right: true));
        var renewed = fixture.Discovery.SnapshotDiagnostics().History.Last(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded);
        var originalRenewed = Assert.Single(originalRecords, entry => entry.ServiceToken == renewed.ServiceToken);
        Assert.Equal(originalRenewed.DeviceToken, renewed.DeviceToken);
        Assert.Equal(originalRenewed.EndpointToken, renewed.EndpointToken);
        fixture.TickAfter(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        CompletePair(fixture.Catalog);
        Assert.DoesNotContain(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);

        fixture.TickAfter(TimeSpan.FromTicks(1));
        var remaining = Assert.Single(fixture.Catalog);
        Assert.Equal(1, fixture.Discovery.Snapshot.CachedRecords);
        Assert.True(remaining.Online);
        var removals = fixture.Discovery.SnapshotDiagnostics().History.Where(entry => entry.Event == DiscoveryTraceEvent.RecordRemoved).ToArray();
        Assert.Equal(3, removals.Length);
        Assert.Equal(3, removals.Select(entry => entry.ServiceToken).Distinct().Count());
        Assert.All(removals, entry =>
        {
            Assert.Equal(DiscoveryTraceReason.ResumeGraceExpired, entry.Reason);
            Assert.Contains(entry.ServiceToken, originalTokens);
            Assert.NotEqual(renewed.ServiceToken, entry.ServiceToken);
        });
        if (renewAirplay)
        {
            Assert.Equal("stereo:" + StereoId, remaining.Id);
            Assert.Equal(RightId, Assert.Single(remaining.Members).Id);
            Assert.Equal(1, remaining.GroupDisplayMemberCount);
            RejectIncompleteAdmission(remaining, fixture.Catalog);
        }
        else
        {
            Assert.Equal(RightId, remaining.Id);
            Assert.False(remaining.IsGroup);
            Assert.Empty(remaining.StereoId);
        }

        foreach (var record in Records()) fixture.Publish(record);
        CompletePair(fixture.Catalog);
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
    }

    [Fact]
    public void MemberGoodbyesDuringContinuousBrowseExposeIncompleteGroupUntilReappearance()
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair();
        var epoch = fixture.Discovery.Snapshot.Epoch;
        var browsers = fixture.Dns.Operations.Count(operation => !operation.IsResolve);
        var leftTokens = fixture.Discovery.SnapshotDiagnostics().History
            .Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded).Take(2).Select(entry => entry.ServiceToken).ToArray();

        fixture.Announce(AirplayRecord(right: false), ttl: 0);
        fixture.Announce(RaopRecord(right: false), ttl: 0);
        var partial = Assert.Single(fixture.Catalog);
        Assert.Equal(RightId, Assert.Single(partial.Members).Id);
        Assert.Equal(1, partial.GroupDisplayMemberCount);
        Assert.True(partial.Online);
        RejectIncompleteAdmission(partial, fixture.Catalog);
        Assert.Equal(epoch, fixture.Discovery.Snapshot.Epoch);
        Assert.Equal(browsers, fixture.Dns.Operations.Count(operation => !operation.IsResolve));
        Assert.Equal(2, fixture.Discovery.Snapshot.CachedRecords);
        var removals = fixture.Discovery.SnapshotDiagnostics().History.Where(entry => entry.Event == DiscoveryTraceEvent.RecordRemoved).ToArray();
        Assert.Equal(2, removals.Length);
        Assert.Equal(2, removals.Select(entry => entry.ServiceToken).Distinct().Count());
        Assert.All(removals, entry =>
        {
            Assert.Equal(DiscoveryTraceReason.PtrGoodbye, entry.Reason);
            Assert.Contains(entry.ServiceToken, leftTokens);
        });

        fixture.Publish(AirplayRecord(right: false));
        fixture.Publish(RaopRecord(right: false));
        CompletePair(fixture.Catalog);
        Assert.Equal(epoch, fixture.Discovery.Snapshot.Epoch);
    }

    [Fact]
    public void ContinuousBrowsePtrExpiryIsDistinctFromResumeGraceAndRecovers()
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair(leftTtl: 1);
        var epoch = fixture.Discovery.Snapshot.Epoch;
        var original = fixture.Discovery.SnapshotDiagnostics().History.ToArray();
        var leftTokens = original.Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded)
            .Take(2).Select(entry => entry.ServiceToken).ToArray();

        fixture.TickAfter(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        CompletePair(fixture.Catalog);
        Assert.DoesNotContain(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);
        fixture.TickAfter(TimeSpan.FromTicks(1));

        var partial = Assert.Single(fixture.Catalog);
        Assert.Equal(RightId, Assert.Single(partial.Members).Id);
        RejectIncompleteAdmission(partial, fixture.Catalog);
        Assert.Equal(epoch, fixture.Discovery.Snapshot.Epoch);
        var history = fixture.Discovery.SnapshotDiagnostics().History;
        var removals = history.Where(entry => entry.Event == DiscoveryTraceEvent.RecordRemoved).ToArray();
        Assert.Equal(2, removals.Length);
        Assert.Equal(2, removals.Select(entry => entry.ServiceToken).Distinct().Count());
        Assert.All(removals, entry =>
        {
            Assert.Equal(DiscoveryTraceReason.PtrExpired, entry.Reason);
            Assert.Equal(1000, entry.ElapsedMs);
            Assert.Contains(entry.ServiceToken, leftTokens);
        });
        Assert.DoesNotContain(history, entry => entry.Reason == DiscoveryTraceReason.ResumeGraceExpired || entry.Event == DiscoveryTraceEvent.Stopped);
        Assert.Equal(original.Count(entry => entry.Event == DiscoveryTraceEvent.Restarted), history.Count(entry => entry.Event == DiscoveryTraceEvent.Restarted));

        fixture.Publish(AirplayRecord(right: false));
        fixture.Publish(RaopRecord(right: false));
        CompletePair(fixture.Catalog);
    }

    [Fact]
    public void LosingOnlyStereoMetadataServiceLeavesItsRaopPeerStandalone()
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair();

        fixture.Announce(AirplayRecord(right: true), ttl: 0);
        Assert.Equal(3, fixture.Discovery.Snapshot.CachedRecords);
        var removed = Assert.Single(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);
        Assert.Equal(DiscoveryTraceReason.PtrGoodbye, removed.Reason);
        Assert.Equal(DiscoveryServiceType.Airplay, removed.ServiceType);
        Assert.True(removed.StereoMetadataPresent);
        var partial = Assert.Single(fixture.Catalog, receiver => receiver.IsGroup);
        Assert.Equal(LeftId, Assert.Single(partial.Members).Id);
        RejectIncompleteAdmission(partial, fixture.Catalog);
        var standalone = Assert.Single(fixture.Catalog, receiver => !receiver.IsGroup);
        Assert.Equal(RightId, standalone.Id);
        Assert.True(standalone.Online);
        Assert.Empty(standalone.StereoId);

        fixture.Publish(AirplayRecord(right: true));
        CompletePair(fixture.Catalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedAdvertisementRemovalIsRecordedWithoutInventingCachedMetadata(bool expires)
    {
        using var fixture = new Fixture();
        fixture.Discovery.Start();
        var record = AirplayRecord(right: false);
        fixture.Announce(record, ttl: 1);
        var pending = Assert.IsType<Operation>(fixture.Dns.Pending(record.Instance));
        if (expires) fixture.TickAfter(TimeSpan.FromSeconds(1));
        else fixture.Announce(record, ttl: 0);
        var history = fixture.Discovery.SnapshotDiagnostics().History;
        var added = Assert.Single(history, entry => entry.Event == DiscoveryTraceEvent.AdvertisementAdded);
        var removed = Assert.Single(history, entry => entry.Event == DiscoveryTraceEvent.AdvertisementRemoved);
        Assert.Equal(expires ? DiscoveryTraceReason.PtrExpired : DiscoveryTraceReason.PtrGoodbye, removed.Reason);
        Assert.Equal(added.ServiceToken, removed.ServiceToken);
        Assert.Null(removed.DeviceToken); Assert.Null(removed.StereoMetadataPresent);
        Assert.DoesNotContain(history, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves); Assert.Empty(fixture.Catalog);
        pending.Resolve(record);
        Assert.DoesNotContain(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded);
    }

    [Fact]
    public void RefreshTimeoutAndLateMetadataCannotUnpairCurrentCachedStereoEvidence()
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair();
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        var lateRight = Assert.IsType<Operation>(fixture.Dns.Pending(AirplayRecord(right: true).Instance));
        fixture.TickAfter(TimeSpan.FromSeconds(5));

        CompletePair(fixture.Catalog);
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.True(lateRight.IsSettled);
        Assert.DoesNotContain(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);
        var accepted = fixture.Discovery.SnapshotDiagnostics().History.Count(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded);
        lateRight.Resolve(AirplayRecord(right: true) with { Txt = new Dictionary<string, string> { ["deviceid"] = RightId } });
        CompletePair(fixture.Catalog);
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
        Assert.Equal(accepted, fixture.Discovery.SnapshotDiagnostics().History.Count(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded));
    }

    [Fact]
    public void ExplicitCurrentTxtClearDiffersFromServiceLossAndCanRecoverOnRefresh()
    {
        using var fixture = new Fixture();
        fixture.ResolveCompletePair();
        var right = AirplayRecord(right: true);
        var previous = fixture.Discovery.SnapshotDiagnostics().History.Last(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded && entry.ServiceType == DiscoveryServiceType.Airplay);
        Assert.True(previous.StereoMetadataPresent);
        var beforeRefresh = fixture.Discovery.SnapshotDiagnostics().RecordedEvents;
        var cleared = new Dictionary<string, string>(right.Txt, StringComparer.OrdinalIgnoreCase)
        {
            ["tsid"] = ""
        };
        fixture.Refresh(Records().Select(record => record.Instance == right.Instance ? record with { Txt = cleared } : record));

        // Every service remains cached. A successful current TXT replacement,
        // unlike missing resolution data, explicitly cleared this association.
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
        var refreshed = fixture.Discovery.SnapshotDiagnostics().History.Where(entry => entry.Sequence > beforeRefresh).ToArray();
        Assert.DoesNotContain(refreshed, entry => entry.Event == DiscoveryTraceEvent.RecordRemoved);
        var accepted = Assert.Single(refreshed, entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded && entry.ServiceToken == previous.ServiceToken);
        Assert.False(accepted.StereoMetadataPresent);
        Assert.Null(accepted.StereoToken);
        Assert.Equal(previous.DeviceToken, accepted.DeviceToken);
        Assert.Equal(previous.EndpointToken, accepted.EndpointToken);
        var partial = Assert.Single(fixture.Catalog, receiver => receiver.IsGroup);
        Assert.Equal(LeftId, Assert.Single(partial.Members).Id);
        RejectIncompleteAdmission(partial, fixture.Catalog);
        var standalone = Assert.Single(fixture.Catalog, receiver => !receiver.IsGroup);
        Assert.Equal(RightId, standalone.Id);
        Assert.Empty(standalone.StereoId);
        Assert.True(standalone.Online);

        fixture.Refresh(Records());
        CompletePair(fixture.Catalog);
        Assert.Equal(4, fixture.Discovery.Snapshot.CachedRecords);
        var recovered = fixture.Discovery.SnapshotDiagnostics().History.Last(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded && entry.ServiceToken == previous.ServiceToken);
        Assert.True(recovered.StereoMetadataPresent);
        Assert.Equal(previous.StereoToken, recovered.StereoToken);
    }

    [Fact]
    public void ConflictingRaopIdentityDoesNotAcquireAnAnonymousDeviceCorrelation()
    {
        using var fixture = new Fixture();
        fixture.Discovery.Start();
        var raop = RaopRecord(right: false) with
        {
            Txt = new Dictionary<string, string> { ["deviceid"] = RightId, ["am"] = "FixtureSpeaker" }
        };
        fixture.Publish(raop);

        var accepted = Assert.Single(fixture.Discovery.SnapshotDiagnostics().History, entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded);
        Assert.Equal(DiscoveryServiceType.Raop, accepted.ServiceType);
        Assert.NotNull(accepted.ServiceToken);
        Assert.NotNull(accepted.EndpointToken);
        Assert.Null(accepted.DeviceToken);
    }

    [Fact]
    public async Task DiagnosticSnapshotsAreReentrantConcurrentBoundedAndAvailableAfterDisposal()
    {
        using var fixture = new Fixture();
        IDiscoveryDiagnostics provider = fixture.Discovery;
        var callbacks = new List<DiscoveryDiagnosticsSnapshot>();
        fixture.Discovery.Changed += _ => callbacks.Add(provider.SnapshotDiagnostics());
        fixture.ResolveCompletePair();
        Assert.NotEmpty(callbacks);
        var initial = provider.SnapshotDiagnostics();
        var originalServiceTokens = initial.History.Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded)
            .Select(entry => entry.ServiceToken).Distinct().ToArray();

        using var cancellation = new CancellationTokenSource();
        using var phase = new Barrier(2);
        var concurrent = new List<DiscoveryDiagnosticsSnapshot>();
        Task RunPhases(Action action) => Task.Run(() =>
        {
            try
            {
                for (var iteration = 0; iteration < 16; iteration++)
                {
                    Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token), "Concurrent snapshot start gate timed out.");
                    action();
                    Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token), "Concurrent snapshot completion gate timed out.");
                }
            }
            catch { cancellation.Cancel(); throw; }
        });
        var reader = RunPhases(() =>
        {
            // This task never touches the fake DNS lists or mutates discovery.
            for (var sample = 0; sample < 8; sample++) concurrent.Add(provider.SnapshotDiagnostics());
        });
        var writer = RunPhases(() =>
        {
            fixture.Discovery.Stop();
            fixture.Discovery.Start();
            foreach (var record in Records()) fixture.Publish(record);
        });
        try { await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            cancellation.Cancel();
            // Cancel releases either barrier wait; join both workers before
            // disposing the barrier or discovery, without masking the first fault.
            await Task.WhenAll(reader, writer).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        Assert.Equal(128, concurrent.Count);
        Assert.All(callbacks.Concat(concurrent), OrderedBoundedHistory);
        var final = provider.SnapshotDiagnostics();
        OrderedBoundedHistory(final);
        Assert.Equal(DiscoveryDiagnosticsSnapshot.HistoryCapacity, final.History.Count);
        Assert.True(final.OverwrittenEvents > 0);
        Assert.All(final.History.Where(entry => entry.Event == DiscoveryTraceEvent.ResolveSucceeded),
            entry => Assert.Contains(entry.ServiceToken, originalServiceTokens));

        fixture.Discovery.Dispose();
        var disposed = provider.SnapshotDiagnostics();
        Assert.True(disposed.Disposed);
        Assert.Equal(0, disposed.CachedRecords);
        Assert.Equal(0, disposed.NativeOwners);
        OrderedBoundedHistory(disposed);
        Assert.Equal(DiscoveryTraceEvent.Disposed, disposed.History.Last().Event);
        Assert.Equal(disposed.RecordedEvents, provider.SnapshotDiagnostics().RecordedEvents);
    }

    private static void OrderedBoundedHistory(DiscoveryDiagnosticsSnapshot snapshot)
    {
        Assert.InRange(snapshot.History.Count, 1, DiscoveryDiagnosticsSnapshot.HistoryCapacity);
        Assert.Equal(snapshot.RecordedEvents - snapshot.History.Count, snapshot.OverwrittenEvents);
        Assert.Equal(snapshot.RecordedEvents, snapshot.History.Last().Sequence);
        for (var index = 1; index < snapshot.History.Count; index++)
            Assert.True(snapshot.History[index - 1].Sequence < snapshot.History[index].Sequence);
    }

    private static Receiver CompletePair(IReadOnlyList<Receiver> catalog)
    {
        var pair = Assert.Single(catalog);
        Assert.Equal("stereo:" + StereoId, pair.Id);
        Assert.True(pair.Online);
        Assert.True(pair.Complete);
        Assert.Equal(2, pair.GroupDisplayMemberCount);
        Assert.Equal(new[] { LeftId, RightId }, pair.Members.Select(member => member.Id));
        Assert.All(pair.Members, member => Assert.True(member.Online));
        return pair;
    }

    private static void RejectIncompleteAdmission(Receiver group, IReadOnlyList<Receiver> catalog)
    {
        Assert.True(group.IsGroup);
        Assert.False(group.Complete);
        Assert.Same(group, ReceiverSelection.ResolvePlayback(Assert.Single(group.Members), catalog));
        Assert.False(ReceiverSelection.IsAutoConnectEligible(group, new() { AutoConnectOnDiscover = true }, new HashSet<string>()));
    }

    private static ServiceRecord[] Records() =>
        [AirplayRecord(right: false), RaopRecord(right: false), AirplayRecord(right: true), RaopRecord(right: true)];

    private static ServiceRecord AirplayRecord(bool right) => new(
        (right ? "FixtureRight" : "FixtureLeft") + "." + Airplay, Airplay, right ? "192.0.2.11" : "192.0.2.10", 7000,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deviceid"] = right ? "AA:BB:CC:DD:EE:02" : "AA:BB:CC:DD:EE:01",
            ["model"] = "FixtureSpeaker", ["tsid"] = StereoId, ["gpn"] = "Fixture stereo", ["igl"] = right ? "0" : "1"
        });

    private static ServiceRecord RaopRecord(bool right) => new(
        (right ? "AABBCCDDEE02@FixtureRight" : "AABBCCDDEE01@FixtureLeft") + "." + Raop, Raop,
        right ? "192.0.2.11" : "192.0.2.10", 7000,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["am"] = "FixtureSpeaker", ["cn"] = "0,1,2,3" });

    private sealed class Fixture : IDisposable
    {
        private readonly ManualClock _clock = new();
        public FakeDns Dns { get; } = new();
        public WindowsDiscovery Discovery { get; }
        public IReadOnlyList<Receiver> Catalog { get; private set; } = [];
        public Fixture()
        {
            Discovery = new(Dns, _clock, () => [], _ => { });
            Discovery.Changed += receivers => Catalog = receivers;
        }
        public void ResolveCompletePair(uint leftTtl = 120)
        {
            Discovery.Start();
            foreach (var record in Records()) Publish(record, record.Address == "192.0.2.10" ? leftTtl : 120);
        }
        public void Announce(ServiceRecord record, uint ttl = 120) => Dns.Browser(record.ServiceType).Announce(record.Instance, ttl);
        public void Publish(ServiceRecord record, uint ttl = 120)
        {
            Announce(record, ttl);
            // An unexpired identical cached record can have no new request due.
            var pending = Dns.Pending(record.Instance);
            pending?.Resolve(record);
        }
        public void Refresh(IEnumerable<ServiceRecord> records)
        {
            TickAfter(TimeSpan.FromSeconds(20));
            foreach (var record in records) Assert.IsType<Operation>(Dns.Pending(record.Instance)).Resolve(record);
        }
        public void TickAfter(TimeSpan elapsed) { _clock.Advance(elapsed); Discovery.Tick(); }
        public void Dispose() => Discovery.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class FakeDns : IDnsServiceApi
    {
        public List<Operation> Operations { get; } = [];
        public Operation Browser(string type) => Operations.Last(operation => !operation.IsResolve && !operation.IsSettled && operation.Type == type);
        public Operation? Pending(string name) => Operations.LastOrDefault(operation => operation.IsResolve && !operation.IsSettled && operation.Name == name);
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback)
            => Create(type, type, callback, null);
        public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback)
            => Create(name, type, null, callback);
        private Operation Create(string name, string type, Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve)
        {
            var operation = new Operation(Operations.Count + 1, name, type, browse, resolve);
            Operations.Add(operation);
            return operation;
        }
    }

    // Cancel settles this fake synchronously by contract, not by inference about Windows callbacks.
    private sealed class Operation(long token, string name, string type,
        Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve) : IDnsServiceOperation
    {
        public long Token => token;
        public string Name => name;
        public string Type => type;
        public bool IsResolve => resolve is not null;
        public bool IsSettled { get; private set; }
        public event Action? Settled;
        public event Action<string>? CancellationFailed { add { } remove { } }
        public event Action<string>? CallbackFailed { add { } remove { } }
        public uint Start() => 9506;
        public uint Cancel()
        {
            if (!IsSettled) { IsSettled = true; Settled?.Invoke(); }
            return 0;
        }
        public void Announce(string instance, uint ttl) => browse!(0, [new(instance, ttl)]);
        public void Resolve(ServiceRecord record) => resolve!(0, record);
    }
}
