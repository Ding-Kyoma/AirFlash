using AirFlash.App.Services;
using AirFlash.Core;
using System.Diagnostics;
using Xunit;
namespace AirFlash.Tests;

// The actual coordinator is source-linked into this assembly. Time and DNS callbacks
// are controlled independently: requesting cancellation is not proof of settlement.
public sealed class DiscoveryLifecycleTests
{
    private const string Airplay = "_airplay._tcp.local";
    private const string Name = "Office._airplay._tcp.local";

    [Theory]
    [InlineData("features", "0x1,0x200")]
    [InlineData("ft", "0x1,0x200")]
    [InlineData("pw", "true")]
    [InlineData("osvers", "27.0")]
    [InlineData("pgid", "")]
    public void CurrentReceiverProfileMetadataRefreshReachesAggregation(string key, string value)
    {
        using var fixture = new Fixture();
        var txt = ProfileTxt();
        if (key == "ft") { txt.Remove("features"); txt["ft"] = "0x0"; }
        if (key == "pgid") { txt["osvers"] = "27.0"; txt["pgid"] = "synthetic-old-group"; }
        var original = Record("192.0.2.1") with { Txt = txt };
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[^1].Resolved(0, original);
        var initial = Assert.Single(fixture.Catalog);
        Assert.Equal(0ul, initial.Features);
        Assert.False(initial.RequiresPassword);
        Assert.False(initial.FollowReceiverClock);
        var publications = fixture.EventCount;

        fixture.TickAfter(TimeSpan.FromSeconds(20));
        fixture.Dns.Resolves[^1].Resolved(0, original with { Txt = new Dictionary<string, string>(txt) { [key] = value } });

        Assert.Equal(publications + 1, fixture.EventCount);
        var updated = Assert.Single(fixture.Catalog);
        Assert.Equal(key is "features" or "ft" ? (1ul << 41) | 1 : 0ul, updated.Features);
        Assert.Equal(key == "pw", updated.RequiresPassword);
        Assert.Equal(key is "osvers" or "pgid", updated.FollowReceiverClock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObsoleteResolveCannotRevertCurrentProfileMetadata(bool previousEpoch)
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Adapters = [new("wifi", "Synthetic adapter", "Synthetic", "192.0.2.2", true, 17)];
        fixture.StartAndAnnounce();
        var obsolete = fixture.Dns.Resolves[^1];
        if (previousEpoch) { fixture.Discovery.SetInterface("wifi"); fixture.Announce(); }
        else { fixture.TickAfter(TimeSpan.FromSeconds(5)); fixture.TickAfter(TimeSpan.FromSeconds(1)); }
        var current = fixture.Dns.Resolves[^1];
        Assert.NotSame(obsolete, current);
        var newest = ProfileTxt();
        newest["features"] = "0x1,0x200"; newest["pw"] = "true"; newest["osvers"] = "27.0";
        current.Resolved(0, Record("192.0.2.1") with { Txt = newest });
        var accepted = Assert.Single(fixture.Catalog);
        Assert.True(accepted.RequiresPassword);
        Assert.True(accepted.FollowReceiverClock);
        Assert.Equal((1ul << 41) | 1, accepted.Features);
        var publications = fixture.EventCount;

        obsolete.Resolved(0, Record("192.0.2.1") with { Txt = ProfileTxt() });

        Assert.Equal(publications, fixture.EventCount);
        Assert.Same(accepted, Assert.Single(fixture.Catalog));
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
    }

    private static Dictionary<string, string> ProfileTxt() => new()
    {
        ["deviceid"] = "synthetic-profile", ["model"] = "AudioAccessory5,1", ["igl"] = "1",
        ["features"] = "0x0", ["pw"] = "false", ["osvers"] = "26.0", ["pgid"] = ""
    };

    [Theory]
    [InlineData("gpn", "After")]
    [InlineData("igl", "1")]
    [InlineData("cn", "0,1")]
    public void TxtOnlyRefreshPublishesUpdatedReceiverMetadata(string key, string value)
    {
        using var fixture = new Fixture();
        const string type = "_raop._tcp.local", name = "Synthetic._raop._tcp.local";
        var txt = new Dictionary<string, string>
        {
            ["deviceid"] = "synthetic-device", ["tsid"] = "synthetic-group",
            ["gpn"] = "Before", ["igl"] = "0", ["cn"] = "0"
        };
        var original = new ServiceRecord(name, type, "192.0.2.1", 7000, txt);
        fixture.Discovery.Start();
        fixture.Dns.Browses.Single(operation => operation.Name == type).Announced(0, new DnsAnnouncement(name, 3600));
        fixture.Dns.Resolves[^1].Resolved(0, original);
        var initial = Assert.Single(fixture.Catalog);
        var publications = fixture.EventCount;
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        fixture.Dns.Resolves[^1].Resolved(0, original with { Txt = new Dictionary<string, string>(txt) { [key] = value } });
        Assert.Equal(publications + 1, fixture.EventCount);
        var updated = Assert.Single(fixture.Catalog);
        if (key == "gpn") Assert.Equal(value, updated.Name);
        if (key == "igl")
        {
            Assert.True(Assert.Single(updated.Members).IsLeader);
            Assert.NotEqual(initial.TransportKey, updated.TransportKey);
        }
        if (key == "cn") Assert.Equal(new byte[] { 0, 1 }, Assert.Single(updated.Members).Codecs);
    }

    [Fact]
    public void PublicKeyOnlyRefreshPublishesChangedEndpointCompatibility()
    {
        using var fixture = new Fixture();
        const string airplayName = "Primary._airplay._tcp.local", raopName = "Secondary._raop._tcp.local", raop = "_raop._tcp.local";
        var first = new ServiceRecord(airplayName, Airplay, "192.0.2.1", 7000,
            new Dictionary<string, string> { ["deviceid"] = "synthetic-primary", ["pk"] = "same-key" });
        var second = new ServiceRecord(raopName, raop, first.Address, first.Port,
            new Dictionary<string, string> { ["deviceid"] = "synthetic-secondary", ["pk"] = "same-key" });
        fixture.Discovery.Start();
        fixture.Dns.Browses.Single(operation => operation.Name == Airplay).Announced(0, new DnsAnnouncement(airplayName, 3600));
        fixture.Dns.Resolves[^1].Resolved(0, first);
        fixture.Dns.Browses.Single(operation => operation.Name == raop).Announced(0, new DnsAnnouncement(raopName, 3600));
        fixture.Dns.Resolves[^1].Resolved(0, second);
        Assert.Single(fixture.Catalog);
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        fixture.Dns.Resolves.Last(operation => operation.Name == raopName).Resolved(0,
            second with { Txt = new Dictionary<string, string>(second.Txt) { ["pk"] = "different-key" } });
        Assert.Equal(2, fixture.Catalog.Count);
        Assert.Contains(fixture.Catalog, receiver => receiver.DeviceId == "synthetic-secondary");
    }

    [Fact]
    public async Task UnchangedOrReorderedTxtRefreshDoesNotRepublishCatalog()
    {
        using var fixture = new Fixture();
        var original = Record("192.0.2.1") with
        {
            Txt = new Dictionary<string, string> { ["deviceid"] = "synthetic-device", ["gpn"] = "Same", ["igl"] = "1" }
        };
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[^1].Resolved(0, original);
        await fixture.Dns.Resolves[^1].DeferredCancellation.WaitAsync(TimeSpan.FromSeconds(5));
        var publications = fixture.EventCount;
        foreach (var txt in new[] { original.Txt, original.Txt.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value) })
        {
            var count = fixture.Dns.Resolves.Count;
            fixture.TickAfter(TimeSpan.FromSeconds(20));
            Assert.Equal(count + 1, fixture.Dns.Resolves.Count);
            fixture.Dns.Resolves[^1].Resolved(0, original with { Txt = txt });
            await fixture.Dns.Resolves[^1].DeferredCancellation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(publications, fixture.EventCount);
        }
    }

    [Fact]
    public void MissingInitialCallbackTimesOutAndRetriesOnlyWhenDue()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce();
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Single(fixture.Dns.Resolves);
        Assert.Equal(1, fixture.Dns.Resolves[0].CancelCalls);
        fixture.TickAfter(TimeSpan.FromMilliseconds(999));
        Assert.Single(fixture.Dns.Resolves);
        fixture.TickAfter(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
    }

    [Fact]
    public void FailedResolvesBackOffExponentiallyAndRepeatedPtrsDoNotResetFailures()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce();
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            fixture.Dns.Resolves[^1].Resolved(1460, null);
            var started = fixture.Dns.Resolves.Count;
            fixture.Announce();
            Assert.Equal(started, fixture.Dns.Resolves.Count);
            fixture.TickAfter(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
            Assert.Equal(started, fixture.Dns.Resolves.Count);
            fixture.TickAfter(TimeSpan.FromTicks(1));
            Assert.Equal(started + 1, fixture.Dns.Resolves.Count);
        }
    }

    [Fact]
    public void SuccessfulResolveRefreshesWhenDueAndKeepsCatalogDuringRetry()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        Assert.Equal("10.0.0.1", Assert.Single(fixture.Catalog).Address);
        fixture.TickAfter(TimeSpan.FromSeconds(20) - TimeSpan.FromTicks(1));
        Assert.Single(fixture.Dns.Resolves);
        fixture.TickAfter(TimeSpan.FromTicks(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        fixture.Dns.Resolves[^1].Resolved(5, null);
        Assert.Single(fixture.Catalog);
        Assert.Equal(1, fixture.Discovery.Snapshot.CachedRecords);
        Assert.Equal(1, fixture.Discovery.Snapshot.AdvertisedNames);
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(3, fixture.Dns.Resolves.Count);
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(2, fixture.Discovery.Snapshot.ActiveQueries);
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.99"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Fact]
    public void RefreshTimeoutKeepsTheCachedCatalogAndIgnoresTheLateResult()
    {
        List<string> logs = [];
        using var fixture = new Fixture(logs.Add);
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(0, Record("192.0.2.1"));
        var original = Assert.Single(fixture.Catalog);
        var publications = 0;
        fixture.Discovery.Changed += _ => publications++;
        var failures = new List<DiscoveryFailure>();
        fixture.Discovery.Failed += failures.Add;

        fixture.TickAfter(TimeSpan.FromSeconds(20));
        var refresh = fixture.Dns.Resolves[^1];
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Equal(1, refresh.CancelCalls);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        var warning = Assert.Single(fixture.Failures);
        Assert.Equal(L.Format("Receiver resolution timed out: {0}. Discovery will retry with backoff.", Name), warning);
        Assert.Single(logs, message => message == warning);
        var failure = Assert.Single(failures);
        Assert.Equal(DiscoveryFailureReason.ResolveTimeout, failure.Reason);
        Assert.Equal(new DiscoveryResolveContext("AA:BB:CC:DD:EE:FF", "192.0.2.1", 7000, ""), failure.Resolve);
        var trace = Assert.Single(fixture.Traces(DiscoveryTraceEvent.ResolveFailed));
        Assert.Equal(DiscoveryTraceReason.ResolveTimeout, trace.Reason);

        refresh.Resolved(0, Record("192.0.2.2"));
        Assert.Equal(0, publications);
        Assert.Same(original, Assert.Single(fixture.Catalog));
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(3, fixture.Dns.Resolves.Count);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        refresh.Resolved(5, null);
        Assert.Equal(trace, Assert.Single(fixture.Traces(DiscoveryTraceEvent.ResolveFailed)));
    }

    [Fact]
    public void InitialResolveFailureHasNoConfirmedIdentity()
    {
        using var fixture = new Fixture();
        var failures = new List<DiscoveryFailure>();
        fixture.Discovery.Failed += failures.Add;
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(13, null);

        var failure = Assert.Single(failures);
        Assert.Equal(DiscoveryFailureReason.ResolveFailed, failure.Reason);
        Assert.Null(failure.Resolve);
        Assert.Empty(fixture.Catalog);
        Assert.Equal(DiscoveryTraceReason.ResolveFailed, Assert.Single(fixture.Traces(DiscoveryTraceEvent.ResolveFailed)).Reason);
    }

    [Fact]
    public void ResolveSuccessRecoversAnUnchangedRecordAndRejectsTheRetiredResult()
    {
        using var fixture = new Fixture();
        var successes = new List<DiscoveryResolveAttempt>();
        var failures = new List<DiscoveryFailure>();
        fixture.Discovery.ResolveSucceeded += successes.Add;
        fixture.Discovery.Failed += failures.Add;
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(0, Record("192.0.2.1"));
        Assert.Single(successes);
        var publications = 0;
        fixture.Discovery.Changed += _ => publications++;

        fixture.TickAfter(TimeSpan.FromSeconds(20));
        var retired = fixture.Dns.Resolves[^1];
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        var failure = Assert.Single(failures).Attempt!;
        Assert.Equal(Name, failure.Instance);
        Assert.Equal(Airplay, failure.ServiceType);
        retired.Resolved(0, Record("192.0.2.2"));
        Assert.Single(successes);

        fixture.TickAfter(TimeSpan.FromSeconds(1));
        fixture.Dns.Resolves[^1].Resolved(0, Record("192.0.2.1"));
        Assert.Equal(2, successes.Count);
        var recovery = successes[^1];
        Assert.Equal(failure.Epoch, recovery.Epoch);
        Assert.True(recovery.Sequence > failure.Sequence);
        Assert.True(failure.SameService(recovery));
        Assert.Equal(0, publications);
    }

    [Fact]
    public void SuccessfulResolveAfterBrowseRestartSupersedesTheOldFailure()
    {
        using var fixture = new Fixture();
        var successes = new List<DiscoveryResolveAttempt>();
        var failures = new List<DiscoveryFailure>();
        fixture.Discovery.ResolveSucceeded += successes.Add;
        fixture.Discovery.Failed += failures.Add;
        fixture.StartAndAnnounce();
        var retired = fixture.Dns.Resolves[0];
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        var failure = Assert.Single(failures).Attempt!;

        fixture.Discovery.Stop();
        fixture.StartAndAnnounce();
        retired.Resolved(0, Record("192.0.2.2"));
        Assert.Empty(successes);
        fixture.Dns.Resolves[^1].Resolved(0, Record("192.0.2.1"));
        var recovery = Assert.Single(successes);
        Assert.True(recovery.Epoch > failure.Epoch);
        Assert.True(recovery.Sequence > failure.Sequence);
        Assert.True(failure.SameService(recovery));
    }

    [Fact]
    public void ATimeoutLoggerRestartDoesNotPublishAnObsoleteFailure()
    {
        WindowsDiscovery? discovery = null;
        var restarted = false;
        var timeout = L.Format("Receiver resolution timed out: {0}. Discovery will retry with backoff.", Name);
        using var fixture = new Fixture(message =>
        {
            if (!restarted && message == timeout)
            {
                restarted = true;
                discovery!.Stop();
                discovery.Start();
            }
        });
        discovery = fixture.Discovery;
        fixture.StartAndAnnounce();
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.True(restarted);
        Assert.Empty(fixture.Failures);
    }

    [Theory]
    [InlineData("00:11:22:33:44:55", true)]
    [InlineData("66:77:88:99:AA:BB", false)]
    public void RefreshFailureOnlyAttributesAConsistentRaopIdentity(string txtId, bool attributable)
    {
        const string type = "_raop._tcp.local";
        const string name = "001122334455@Synthetic._raop._tcp.local";
        using var fixture = new Fixture();
        var failures = new List<DiscoveryFailure>();
        fixture.Discovery.Failed += failures.Add;
        fixture.Discovery.Start();
        fixture.Dns.Browses.Single(browse => browse.Name == type).Announced(0, new DnsAnnouncement(name, 3600));
        fixture.Dns.Resolves.Single().Resolved(0, new(name, type, "192.0.2.1", 7000, new Dictionary<string, string> { ["deviceid"] = txtId }));
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        fixture.Dns.Resolves[^1].Resolved(13, null);

        var failure = Assert.Single(failures);
        Assert.Equal(DiscoveryFailureReason.ResolveFailed, failure.Reason);
        Assert.Equal(attributable ? new DiscoveryResolveContext(txtId, "192.0.2.1", 7000, "") : null, failure.Resolve);
        Assert.Single(fixture.Catalog);
    }

    [Fact]
    public void PtrExpiryRemovesCatalogAndCancelsPendingRefresh()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce(ttl: 25);
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        var refresh = fixture.Dns.Resolves[^1];
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.AdvertisedNames);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(1, refresh.CancelCalls);
        fixture.TickAfter(TimeSpan.FromMinutes(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
    }

    [Fact]
    public void MonotonicTimeoutAndRetryIgnoreWallClockChanges()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce();
        fixture.Clock.ShiftUtc(TimeSpan.FromDays(30));
        fixture.Discovery.Tick();
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        fixture.Clock.ShiftUtc(TimeSpan.FromDays(-60));
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionAtDeadlineCannotPublishRegardlessOfTimerCallbackOrder(bool tickFirst)
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        if (tickFirst) fixture.Discovery.Tick();
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        if (!tickFirst) fixture.Discovery.Tick();
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
    }

    [Fact]
    public void CompletionImmediatelyBeforeDeadlineIsAccepted()
    {
        using var fixture = new Fixture();
        fixture.StartAndAnnounce();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        Assert.Equal("10.0.0.1", Assert.Single(fixture.Catalog).Address);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionAfterAdvertisementExpiryCannotPublishRegardlessOfTimerOrder(bool tickFirst)
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce(ttl: 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        if (tickFirst) fixture.Discovery.Tick();
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        if (!tickFirst) fixture.Discovery.Tick();
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.AdvertisedNames);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        Assert.Single(fixture.Dns.Resolves);
    }

    [Fact]
    public void SameEpochLateCallbackCannotClearOrPublishReplacement()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce();
        var old = fixture.Dns.Resolves[0];
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        var replacement = fixture.Dns.Resolves[^1];
        Assert.NotEqual(old.Token, replacement.Token);
        old.Resolved(0, Record("10.0.0.1"));
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Empty(fixture.Catalog);
        replacement.Resolved(0, Record("10.0.0.2"));
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Fact]
    public void RemovalThenNewPtrKeepsOldCallbackFromRepopulatingOrRetiringNewRequest()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce();
        var removed = fixture.Dns.Resolves[0];
        fixture.Announce(ttl: 0);
        Assert.Equal(0, fixture.Discovery.Snapshot.AdvertisedNames);
        fixture.Announce();
        var replacement = fixture.Dns.Resolves[^1];
        Assert.NotEqual(removed.Token, replacement.Token);
        removed.Resolved(0, Record("10.0.0.1"));
        Assert.Empty(fixture.Catalog);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        replacement.Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Fact]
    public void OldEpochBrowseAndResolveCallbacksCannotAffectRestartedCatalog()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        fixture.StartAndAnnounce();
        var browse = fixture.Dns.Browses[0];
        var old = fixture.Dns.Resolves[0];
        var epoch = fixture.Discovery.Snapshot.Epoch;
        fixture.Discovery.SetInterface("wifi");
        Assert.True(fixture.Discovery.Snapshot.Epoch > epoch);
        var started = fixture.Dns.Resolves.Count;
        browse.Announced(0, new DnsAnnouncement("Ghost._airplay._tcp.local", 3600));
        old.Resolved(0, Record("10.0.0.1"));
        Assert.Equal(started, fixture.Dns.Resolves.Count);
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.AdvertisedNames);
        fixture.Announce();
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
        Assert.All(fixture.Dns.Browses.Skip(2), operation => Assert.Equal(17u, operation.InterfaceIndex));
    }

    [Fact]
    public void DisposeIsIdempotentAndLateCallbacksRaiseNoEvents()
    {
        var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce();
        var before = fixture.EventCount;
        fixture.Dispose();
        fixture.Dispose();
        fixture.Dns.Browses[0].Announced(0, new DnsAnnouncement(Name, 3600));
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        fixture.Discovery.Tick();
        Assert.Equal(before, fixture.EventCount);
        Assert.All(fixture.Dns.Operations, operation => Assert.Equal(1, operation.CancelCalls));
        fixture.Dns.SettleAll();
        Assert.Equal(fixture.Dns.Allocations, fixture.Dns.Releases);
        Assert.Equal(0, fixture.Discovery.Snapshot.NativeOwners);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(5u, true)]
    [InlineData(5u, false)]
    public void StartWithoutCallbackDoesNotLeaveResolvePendingUnlessDnsReportsPending(uint status, bool settleBeforeReturn)
    {
        using var fixture = new Fixture();
        fixture.Dns.OnCreated = operation =>
        {
            if (!operation.IsResolve) return;
            operation.StartStatus = status;
            operation.SettleOnNonPendingStart = settleBeforeReturn;
        };
        fixture.StartAndAnnounce();
        var failed = Assert.Single(fixture.Dns.Resolves);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Single(fixture.Failures);
        var reason = settleBeforeReturn ? DiscoveryTraceReason.QueryEndedUnexpectedly : DiscoveryTraceReason.QueryStartFailed;
        Assert.Equal(reason, Assert.Single(fixture.Traces(DiscoveryTraceEvent.ResolveFailed)).Reason);
        Assert.Equal(1, failed.ReleaseCalls);

        fixture.Dns.OnCreated = null;
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        failed.Resolved(5, null);
        Assert.Equal(reason, Assert.Single(fixture.Traces(DiscoveryTraceEvent.ResolveFailed)).Reason);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9506u)]
    public void InlineResolveCompletionDuringStartIsAppliedOnce(uint status)
    {
        using var fixture = new Fixture();
        fixture.Dns.OnCreated = operation =>
        {
            if (!operation.IsResolve) return;
            operation.StartStatus = status;
            operation.DuringStart = () => operation.Resolved(0, Record("10.0.0.1"));
        };
        fixture.StartAndAnnounce();
        Assert.Equal("10.0.0.1", Assert.Single(fixture.Catalog).Address);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        fixture.Discovery.Tick();
        Assert.Single(fixture.Dns.Resolves);
        Assert.Equal(1, fixture.Dns.Resolves[0].ReleaseCalls);
        Assert.Empty(fixture.Failures);
    }

    [Fact]
    public void InlineLateCompletionDuringTimeoutCancellationCannotPublish()
    {
        using var fixture = new Fixture();
        fixture.Dns.OnCreated = operation =>
        {
            if (operation.IsResolve) operation.DuringCancel = () => operation.Resolved(0, Record("10.0.0.1"));
        };
        fixture.StartAndAnnounce();
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(1, fixture.Dns.Resolves[0].ReleaseCalls);
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
    }

    [Fact]
    public async Task RestartCanRetireResolveWhileItsNativeStartHasNotReturned()
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        fixture.Dns.OnCreated = operation =>
        {
            if (operation.IsResolve) operation.DuringStart = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "The fixture must release the gated native start.");
                operation.Resolved(0, Record("10.0.0.1"));
            };
        };
        fixture.Discovery.Start();
        var announcement = Task.Run(() => fixture.Announce());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Resolve never reached the native start barrier.");
        try
        {
            // This separate completion must progress before the old start is released.
            await Task.Run(() => fixture.Discovery.SetInterface("wifi")).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
            Assert.Empty(fixture.Catalog);
        }
        finally { release.Set(); }
        await announcement.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(1, fixture.Dns.Resolves[0].CancelCalls);
        Assert.Equal(1, fixture.Dns.Resolves[0].ReleaseCalls);
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
    }

    [Fact]
    public void RestartFromInlineBrowseCallbackCannotPublishOldEpochRecord()
    {
        using var fixture = new Fixture();
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        var restarted = false;
        fixture.Discovery.Changed += receivers =>
        {
            if (restarted || receivers.Count == 0) return;
            restarted = true;
            fixture.Discovery.SetInterface("wifi");
        };
        fixture.Dns.OnCreated = operation =>
        {
            if (operation.IsResolve) operation.DuringStart = () => operation.Resolved(0, Record("10.0.0.1"));
        };
        fixture.StartAndAnnounce();
        Assert.True(restarted);
        Assert.Empty(fixture.Catalog);
        Assert.Equal(0, fixture.Discovery.Snapshot.CachedRecords);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeFromChangedSubscriberCannotCancelWhileHoldingPublicationLock(bool concurrentCallbackDrains)
    {
        using var disposeRequested = new ManualResetEventSlim();
        using var allowOtherPublication = new ManualResetEventSlim();
        using var otherBrowseReturned = new ManualResetEventSlim();
        var firstPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRecordCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationTimedOut = 0;
        var cancellationInsidePublication = 0;
        var nativeCancellationInsidePublication = 0;
        var publicationGateTimedOut = 0;
        var disposeGateTimedOut = 0;
        var firstThread = 0;
        var otherThread = 0;
        var cancellationThread = 0;
        const string otherName = "Kitchen._airplay._tcp.local";
        using var fixture = new Fixture(message =>
        {
            if (!message.Contains(otherName, StringComparison.Ordinal)) return;
            otherRecordCommitted.TrySetResult();
            if (!allowOtherPublication.Wait(TimeSpan.FromSeconds(30))) Interlocked.Exchange(ref publicationGateTimedOut, 1);
        });
        var publication = typeof(WindowsDiscovery).GetField("_publication", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Discovery)!;
        fixture.Dns.OnCreated = operation =>
        {
            if (!operation.IsResolve || operation.Name != otherName) return;
            operation.DuringStart = () =>
            {
                operation.Resolved(0, Record("10.0.0.2") with
                {
                    Instance = otherName,
                    Txt = new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:02" }
                });
            };
        };
        fixture.StartAndAnnounce();
        var browser = fixture.Dns.Browses[0];
        browser.DeferCancellationUntilCallbacksReturn = true;
        browser.DuringCancelRequest = () =>
        {
            cancellationThread = Environment.CurrentManagedThreadId;
            if (Monitor.IsEntered(publication)) Interlocked.Exchange(ref cancellationInsidePublication, 1);
            if (!concurrentCallbackDrains) allowOtherPublication.Set();
            cancellationRequested.TrySetResult();
        };
        browser.DuringCancel = () =>
        {
            if (Monitor.IsEntered(publication)) Interlocked.Exchange(ref nativeCancellationInsidePublication, 1);
            // This is the native join, after the fake operation accepted the request.
            // Like the production adapter, it cannot synchronously join its own callback.
            if (!otherBrowseReturned.Wait(TimeSpan.FromSeconds(10))) Interlocked.Exchange(ref cancellationTimedOut, 1);
        };
        fixture.Discovery.Changed += receivers =>
        {
            if (receivers.Count != 1 || receivers[0].Address != "10.0.0.1") return;
            firstPublication.TrySetResult();
            if (!disposeRequested.Wait(TimeSpan.FromSeconds(30))) Interlocked.Exchange(ref disposeGateTimedOut, 1);
            fixture.Discovery.Dispose();
        };
        Task? first = null;
        Task? other = null;
        try
        {
            first = Task.Factory.StartNew(() =>
            {
                firstThread = Environment.CurrentManagedThreadId;
                try
                {
                    if (concurrentCallbackDrains)
                    {
                        // One test-owned recursion makes the first Publish's finally
                        // defer draining. Releasing it lets the other callback take the
                        // batch deterministically, instead of relying on monitor fairness.
                        lock (publication) fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
                    }
                    else fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
                }
                finally { firstReturned.TrySetResult(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(30));
            other = Task.Factory.StartNew(() =>
            {
                otherThread = Environment.CurrentManagedThreadId;
                try { browser.Announced(0, new DnsAnnouncement(otherName, 3600)); }
                finally { otherBrowseReturned.Set(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await otherRecordCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, fixture.Discovery.Snapshot.CachedRecords);
            disposeRequested.Set();
            if (concurrentCallbackDrains)
            {
                await firstReturned.Task.WaitAsync(TimeSpan.FromSeconds(30));
                allowOtherPublication.Set();
            }
            await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(30));
            await cancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await browser.DeferredCancellation.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(concurrentCallbackDrains ? otherThread : firstThread, cancellationThread);
            Assert.Equal(0, disposeGateTimedOut);
            Assert.Equal(0, publicationGateTimedOut);
            Assert.Equal(0, cancellationInsidePublication);
            Assert.Equal(0, nativeCancellationInsidePublication);
            Assert.Equal(0, cancellationTimedOut);
            Assert.Equal(1, browser.CancelCalls);
            Assert.Equal(1, browser.ReleaseCalls);
            Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
            Assert.Equal(0, fixture.Discovery.Snapshot.CachedRecords);
            Assert.Equal(0, fixture.Discovery.Snapshot.NativeOwners);
        }
        finally
        {
            disposeRequested.Set();
            allowOtherPublication.Set();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(30));
            if (other is not null) await other.WaitAsync(TimeSpan.FromSeconds(30));
            await browser.DeferredCancellation.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void ReentrantRestartsCoalesceBoundedNativeWorkAndNeverStartRetiredEpochs()
    {
        using var fixture = new Fixture();
        fixture.Adapters = Enumerable.Range(1, 40).Select(index => new DiscoveryAdapter($"nic{index}", "NIC", "Physical", "10.0.0.3", true, (uint)index)).ToArray();
        var restarted = false;
        fixture.Discovery.Changed += receivers =>
        {
            if (restarted || receivers.Count == 0) return;
            restarted = true;
            for (var index = 1; index <= 40; index++) fixture.Discovery.SetInterface($"nic{index}");
        };
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        Assert.True(restarted);
        Assert.Equal(65, fixture.Dns.Allocations); // The completed resolve freed one slot before publication.
        Assert.Equal(64, fixture.Dns.MaximumConcurrentOwners);
        Assert.Equal(0, fixture.Discovery.Snapshot.NativeOwners);
        Assert.All(fixture.Dns.Browses.Skip(2), operation => Assert.Equal(0, operation.StartCalls));
        fixture.Discovery.Tick();
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        Assert.All(fixture.Dns.Browses.TakeLast(2), operation =>
        {
            Assert.Equal(1, operation.StartCalls);
            Assert.Equal(40u, operation.InterfaceIndex);
        });
    }

    [Fact]
    public void FailedSubscriberCanRestartAndNewEpochStillStartsAfterPublication()
    {
        using var fixture = new Fixture();
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        var restarted = false;
        fixture.Discovery.Failed += _ =>
        {
            if (restarted) return;
            restarted = true;
            fixture.Discovery.SetInterface("wifi");
        };
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(5, null);
        Assert.True(restarted);
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        Assert.All(fixture.Dns.Browses.Skip(2), operation => Assert.Equal(1, operation.StartCalls));
        fixture.Announce();
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void CancelAndLateResultRetainCapacityUntilExplicitFakeQuiescence(uint status)
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Dns.OnCreated = operation => operation.CancelStatus = status;
        fixture.StartAndAnnounce();
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        fixture.TickAfter(TimeSpan.FromSeconds(2));
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        Assert.Equal(4, fixture.Discovery.Snapshot.NativeOwners);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Contains(fixture.Failures, message => message == L.Get("Some discovery requests are paused because native DNS ownership cannot be safely released. Discovery resumes if ownership settles; restarting the app is the final recovery."));
        fixture.Dns.Resolves[0].Resolved(995, null);
        fixture.Discovery.Tick();
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        Assert.Equal(4, fixture.Discovery.Snapshot.NativeOwners);
        fixture.Dns.Resolves[0].Settle(); // Explicit fake boundary signal, not a DNS result.
        fixture.Discovery.Tick();
        Assert.Equal(3, fixture.Dns.Resolves.Count);
        Assert.Equal(4, fixture.Discovery.Snapshot.NativeOwners);
        fixture.Dns.Resolves[1].Resolved(0, Record("10.0.0.1"));
        Assert.Empty(fixture.Catalog);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Fact]
    public void ThrowingObserversCannotAbandonDeferredCancellationAndStartBatch()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("Fixture log observer failure."));
        fixture.Dns.SettleOnCancel = false;
        fixture.Dns.OnCreated = operation => operation.CancelStatus = 5;
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        fixture.Discovery.Changed += _ => throw new InvalidOperationException("Fixture Changed observer failure.");
        fixture.Discovery.Failed += _ => throw new InvalidOperationException("Fixture Failed observer failure.");
        var cancellationDiagnostics = 0;
        fixture.Discovery.Failed += _ => cancellationDiagnostics++;
        var restarted = false;
        fixture.Discovery.Changed += receivers =>
        {
            if (restarted || receivers.Count == 0) return;
            restarted = true;
            fixture.Discovery.SetInterface("wifi");
        };
        fixture.StartAndAnnounce();
        fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1"));
        Assert.True(restarted, "The throwing Changed subscriber must not prevent the later restart subscriber.");
        Assert.Equal(3, cancellationDiagnostics);
        Assert.Equal(5, fixture.Discovery.Snapshot.NativeOwners);
        Assert.All(fixture.Dns.Browses.Take(2), operation => Assert.Equal(1, operation.CancelCalls));
        Assert.All(fixture.Dns.Browses.Skip(2), operation => Assert.Equal(1, operation.StartCalls));
        fixture.Announce();
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
        Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
    }

    [Theory]
    [InlineData("log")]
    [InlineData("catalog")]
    [InlineData("failure")]
    public void ThrowingTraceFallbackCannotAbandonPublicationOrDeferredNativeWork(string channel)
    {
        var marker = "synthetic-discovery-trace-" + Guid.NewGuid().ToString("N");
        using var fixture = new Fixture(_ =>
        {
            if (channel == "log") throw new InvalidOperationException(marker + " log observer");
        });
        fixture.Dns.SettleOnCancel = false;
        fixture.Dns.OnCreated = operation => operation.CancelStatus = 5;
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        fixture.Discovery.Changed += _ =>
        {
            if (channel == "catalog") throw new InvalidOperationException(marker + " catalog observer");
        };
        fixture.Discovery.Failed += _ =>
        {
            if (channel == "failure") throw new InvalidOperationException(marker + " failure observer");
        };
        var cancellationDiagnostics = 0;
        fixture.Discovery.Failed += _ => cancellationDiagnostics++;
        var laterCatalogs = 0;
        var publishedAddresses = new List<string>();
        var restarted = false;
        fixture.Discovery.Changed += receivers =>
        {
            laterCatalogs++;
            publishedAddresses.AddRange(receivers.Select(receiver => receiver.Address));
            if (restarted || receivers.Count == 0) return;
            restarted = true;
            fixture.Discovery.SetInterface("wifi");
        };
        var trace = new ThrowingDiscoveryTraceListener(marker);
        try
        {
            // Match only this fixture's unique messages; parallel tests keep
            // their own trace listeners and never trigger this injected failure.
            Trace.Listeners.Add(trace);
            fixture.StartAndAnnounce();
            var error = Xunit.Record.Exception(() => fixture.Dns.Resolves[0].Resolved(0, Record("10.0.0.1")));
            Assert.True(trace.ThrowCount > 0, "The secondary trace sink must actually throw.");
            Assert.Null(error);
            Assert.Equal(new[] { "10.0.0.1" }, publishedAddresses);
            Assert.True(restarted, "Fallback failure must not skip a later catalog subscriber.");
            Assert.Equal(3, cancellationDiagnostics);
            Assert.Equal(5, fixture.Discovery.Snapshot.NativeOwners);
            Assert.All(fixture.Dns.Browses.Take(2), operation => Assert.Equal(1, operation.CancelCalls));
            Assert.All(fixture.Dns.Browses.Skip(2), operation => Assert.Equal(1, operation.StartCalls));
            Assert.Equal(2, laterCatalogs);
            fixture.Announce();
            fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.2"));
            Assert.Equal(1, fixture.Discovery.Snapshot.CachedRecords);
            Assert.Equal("10.0.0.2", Assert.Single(fixture.Catalog).Address);
            Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, publishedAddresses);
            Assert.Equal(3, laterCatalogs);
            Assert.Equal(4, cancellationDiagnostics);
        }
        finally
        {
            Trace.Listeners.Remove(trace);
            trace.Dispose();
        }
    }

    [Fact]
    public void QueuedCancellationFailureIsReportedForRetainedOwnerAndIgnoredAfterSettlementOrDispose()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.StartAndAnnounce();
        var operation = fixture.Dns.Resolves[0];
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        Assert.Equal(1, operation.CancellationFailureSubscriberCount);
        operation.EmitCancellationFailure("Fixture cancellation denied.");
        Assert.Equal(1, fixture.Failures.Count(message => message.Contains("Fixture cancellation denied.", StringComparison.Ordinal)));
        Assert.Equal(3, fixture.Discovery.Snapshot.NativeOwners);
        operation.Settle();
        Assert.Equal(0, operation.CancellationFailureSubscriberCount);
        var afterSettlement = fixture.EventCount;
        operation.EmitCancellationFailure("Settled owner must not report.");
        Assert.Equal(afterSettlement, fixture.EventCount);
        fixture.Discovery.Dispose();
        var afterDispose = fixture.EventCount;
        fixture.Dns.Browses[0].EmitCancellationFailure("Disposed discovery must not report.");
        Assert.Equal(afterDispose, fixture.EventCount);
    }

    [Fact]
    public void RestartCountsRetiredBrowsersAndResolvesAgainstTotalOwnerLimit()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Adapters = Enumerable.Range(1, 40).Select(index => new DiscoveryAdapter($"nic{index}", "NIC", "Physical", "10.0.0.3", true, (uint)index)).ToArray();
        fixture.StartAndAnnounce();
        for (var index = 1; index <= 40; index++) fixture.Discovery.SetInterface($"nic{index}");
        Assert.Equal(64, fixture.Dns.Allocations);
        Assert.Equal(64, fixture.Discovery.Snapshot.NativeOwners);
        Assert.NotEmpty(fixture.Failures);
        fixture.Dns.SettleAll();
        fixture.Discovery.Tick();
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        Assert.Equal(66, fixture.Dns.Allocations);
        Assert.All(fixture.Dns.Browses.TakeLast(2), operation => Assert.Equal(40u, operation.InterfaceIndex));
    }

    [Fact]
    public void ManyAdvertisedNamesCannotExceedOwnerLimitAndOneSettlementAdmitsOneRetry()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Discovery.Start();
        var announcements = Enumerable.Range(0, 64).Select(index => new DnsAnnouncement($"Office{index}._airplay._tcp.local", 3600)).ToArray();
        fixture.Dns.Browses[0].Announced(0, announcements);
        Assert.Equal(64, fixture.Discovery.Snapshot.AdvertisedNames);
        Assert.Equal(62, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(64, fixture.Discovery.Snapshot.NativeOwners);
        fixture.TickAfter(TimeSpan.FromSeconds(5));
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(62, fixture.Dns.Resolves.Count);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        fixture.Dns.Resolves[0].Resolved(995, null);
        fixture.Dns.Resolves[0].Settle(); // Fake native quiescence is controlled independently.
        fixture.Discovery.Tick();
        Assert.Equal(63, fixture.Dns.Resolves.Count);
        Assert.Equal(1, fixture.Discovery.Snapshot.PendingResolves);
        Assert.Equal(64, fixture.Discovery.Snapshot.NativeOwners);
    }

    [Fact]
    public void SameNameOwnerLimitIncludesRetiredEpochs()
    {
        using var fixture = new Fixture();
        fixture.Dns.SettleOnCancel = false;
        fixture.Adapters = [new("nic1", "NIC", "Physical", "10.0.0.3", true, 1), new("nic2", "NIC", "Physical", "10.0.0.3", true, 2)];
        fixture.StartAndAnnounce();
        fixture.Discovery.SetInterface("nic1");
        fixture.Announce();
        fixture.Discovery.SetInterface("nic2");
        fixture.Announce();
        Assert.Equal(2, fixture.Dns.Resolves.Count);
        Assert.Equal(0, fixture.Discovery.Snapshot.PendingResolves);
        fixture.Dns.Resolves[0].Resolved(995, null);
        fixture.Dns.Resolves[0].Settle(); // Fake native quiescence is controlled independently.
        fixture.Discovery.Tick();
        Assert.Equal(3, fixture.Dns.Resolves.Count);
        Assert.Equal(2u, fixture.Dns.Resolves[^1].InterfaceIndex);
    }

    [Fact]
    public void MissingSelectedAdapterNeverStartsAllInterfaceBrowseAndRestoresAfterAdapterCheck()
    {
        using var fixture = new Fixture();
        fixture.Discovery.SetInterface("wifi");
        fixture.Discovery.Start();
        Assert.Empty(fixture.Dns.Operations);
        Assert.NotEmpty(fixture.Failures);
        fixture.Adapters = [new("wifi", "Wi-Fi", "Physical", "10.0.0.3", true, 17)];
        fixture.TickAfter(TimeSpan.FromSeconds(19));
        Assert.Empty(fixture.Dns.Operations);
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Dns.Browses.Count);
        Assert.All(fixture.Dns.Browses, operation => Assert.Equal(17u, operation.InterfaceIndex));
        fixture.Adapters = [];
        fixture.TickAfter(TimeSpan.FromSeconds(20));
        Assert.Equal(2, fixture.Dns.Browses.Count);
        Assert.Equal(0, fixture.Discovery.Snapshot.NativeOwners);
        Assert.All(fixture.Dns.Browses, operation => Assert.Equal(1, operation.CancelCalls));
    }

    [Fact]
    public void FailedBrowseStartCanRecoverWithoutCreatingUnboundedOwners()
    {
        using var fixture = new Fixture();
        fixture.Dns.OnCreated = operation => { if (!operation.IsResolve) operation.StartStatus = 5; };
        fixture.Discovery.Start();
        Assert.Equal(2, fixture.Failures.Count);
        Assert.Equal(0, fixture.Discovery.Snapshot.NativeOwners);
        var traces = fixture.Traces(DiscoveryTraceEvent.BrowseFailed);
        Assert.Equal(2, traces.Length);
        Assert.All(traces, trace => Assert.Equal(DiscoveryTraceReason.QueryEndedUnexpectedly, trace.Reason));
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(4, fixture.Failures.Count);
        Assert.Equal(4, fixture.Dns.Browses.Count);
        traces = fixture.Traces(DiscoveryTraceEvent.BrowseFailed);
        Assert.Equal(4, traces.Length);
        Assert.All(traces, trace => Assert.Equal(DiscoveryTraceReason.QueryEndedUnexpectedly, trace.Reason));
        fixture.Dns.OnCreated = null;
        fixture.TickAfter(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(4, fixture.Dns.Browses.Count);
        fixture.TickAfter(TimeSpan.FromTicks(1));
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        fixture.Announce();
        fixture.Dns.Resolves[^1].Resolved(0, Record("10.0.0.1"));
        Assert.Single(fixture.Catalog);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoaderFailureDoesNotEscapeOrPreventStartingTheOtherBrowser(bool settleBeforeThrow)
    {
        using var fixture = new Fixture();
        fixture.Dns.OnCreated = operation =>
        {
            if (operation.Name != Airplay) return;
            operation.DuringStart = () =>
            {
                if (settleBeforeThrow) operation.Settle();
                throw new DllNotFoundException("Fixture DNS loader failure.");
            };
        };
        fixture.Discovery.Start();
        Assert.Equal(2, fixture.Dns.Browses.Count);
        Assert.Equal(1, fixture.Discovery.Snapshot.NativeOwners);
        Assert.Single(fixture.Failures);
        var failed = fixture.Dns.Browses.Single(operation => operation.Name == Airplay);
        var trace = Assert.Single(fixture.Traces(DiscoveryTraceEvent.BrowseFailed));
        Assert.Equal(settleBeforeThrow ? DiscoveryTraceReason.QueryEndedUnexpectedly : DiscoveryTraceReason.QueryStartFailed, trace.Reason);
        Assert.Equal(DiscoveryServiceType.Airplay, trace.ServiceType);
        fixture.Dns.OnCreated = null;
        fixture.TickAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(3, fixture.Dns.Browses.Count);
        Assert.Equal(2, fixture.Discovery.Snapshot.NativeOwners);
        failed.Announced(5);
        Assert.Equal(trace, Assert.Single(fixture.Traces(DiscoveryTraceEvent.BrowseFailed)));
    }

    private static ServiceRecord Record(string address) => new(Name, Airplay, address, 7000, new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:FF" });

    [Fact]
    public async Task ProductionSchedulingKeepsBlockedNativeStartOffTheCallingThread()
    {
        var dns = new FakeDns();
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settlements = new List<Task>();
        var caller = Environment.CurrentManagedThreadId;
        var nativeThread = caller;
        dns.OnCreated = operation =>
        {
            if (operation.Name != Airplay) return;
            // Model native cancellation joining the accepted Start call. The
            // fixture must join settlement after releasing its native barrier.
            operation.DuringCancel = () => operation.StartReturned.Task.GetAwaiter().GetResult();
            operation.DuringStart = () =>
            {
                nativeThread = Environment.CurrentManagedThreadId; entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic start was not released.");
                completed.TrySetResult();
            };
        };
        using var discovery = new WindowsDiscovery(dns, clock, () => [], _ => { }, queueNative: true);
        try
        {
            discovery.Start(); // Must return before the native Start barrier releases.
            // Subscribe after the coordinator so completion also joins its owner removal.
            foreach (var operation in dns.Operations)
            {
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation.Settled += () => settled.TrySetResult();
                settlements.Add(settled.Task);
            }
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotEqual(caller, nativeThread);
            discovery.Stop();
            Assert.Equal(0, discovery.Snapshot.ActiveQueries);
        }
        finally
        {
            release.Set(); discovery.Dispose();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(settlements).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, discovery.Snapshot.NativeOwners);
        Assert.All(dns.Operations, operation => Assert.Equal(1, operation.ReleaseCalls));
    }

    private sealed class ThrowingDiscoveryTraceListener(string marker) : TraceListener
    {
        private int _throws;
        public int ThrowCount => Volatile.Read(ref _throws);
        public override void Write(string? message) => ThrowIfTagged(message);
        public override void WriteLine(string? message) => ThrowIfTagged(message);
        private void ThrowIfTagged(string? message)
        {
            if (message?.Contains(marker, StringComparison.Ordinal) != true) return;
            Interlocked.Increment(ref _throws);
            throw new InvalidOperationException("Synthetic trace listener failure: " + marker);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly FakeDns Dns = new();
        public readonly WindowsDiscovery Discovery;
        public readonly List<string> Failures = [];
        public IReadOnlyList<DiscoveryAdapter> Adapters = [];
        public IReadOnlyList<Receiver> Catalog = [];
        public int EventCount;
        public Fixture(Action<string>? log = null)
        {
            Discovery = new(Dns, Clock, () => Adapters, log ?? (_ => { }));
            Discovery.Changed += receivers => { Catalog = receivers; EventCount++; };
            Discovery.Failed += failure => { Failures.Add(failure.Message); EventCount++; };
        }
        public void StartAndAnnounce(uint ttl = 3600) { Discovery.Start(); Announce(ttl); }
        public void Announce(uint ttl = 3600) => Dns.Browses.Last(operation => operation.Name == Airplay && !operation.CancelRequested).Announced(0, new DnsAnnouncement(Name, ttl));
        public void TickAfter(TimeSpan duration) { Clock.Advance(duration); Discovery.Tick(); }
        public DiscoveryTraceEntry[] Traces(DiscoveryTraceEvent kind) => Discovery.SnapshotDiagnostics().History.Where(entry => entry.Event == kind).ToArray();
        public void Dispose() { Discovery.Dispose(); Dns.SettleAll(); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utc = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan duration) { _timestamp += duration.Ticks; _utc += duration; }
        public void ShiftUtc(TimeSpan duration) => _utc += duration;
    }

    private sealed class FakeDns : IDnsServiceApi
    {
        public readonly List<FakeOperation> Operations = [];
        public List<FakeOperation> Browses => Operations.Where(operation => !operation.IsResolve).ToList();
        public List<FakeOperation> Resolves => Operations.Where(operation => operation.IsResolve).ToList();
        public bool SettleOnCancel = true;
        public Action<FakeOperation>? OnCreated;
        public int Allocations;
        public int Releases;
        public int MaximumConcurrentOwners;
        public IDnsServiceOperation CreateBrowse(string type, uint interfaceIndex, Action<uint, IReadOnlyList<DnsAnnouncement>> callback) => Create(type, type, interfaceIndex, callback, null);
        public IDnsServiceOperation CreateResolve(string name, string type, uint interfaceIndex, Action<uint, ServiceRecord?> callback) => Create(name, type, interfaceIndex, null, callback);
        private FakeOperation Create(string name, string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve)
        {
            var operation = new FakeOperation(this, ++Allocations, name, type, index, browse, resolve);
            Operations.Add(operation);
            MaximumConcurrentOwners = Math.Max(MaximumConcurrentOwners, Allocations - Releases);
            OnCreated?.Invoke(operation);
            return operation;
        }
        public void SettleAll() { foreach (var operation in Operations.ToArray()) operation.Settle(); }
    }

    private sealed class FakeOperation(FakeDns owner, long token, string name, string type, uint index,
        Action<uint, IReadOnlyList<DnsAnnouncement>>? browse, Action<uint, ServiceRecord?>? resolve) : IDnsServiceOperation
    {
        private readonly object _callbackGate = new();
        private int _activeCallbacks;
        private bool _cancelDeferred;
        public long Token { get; } = token;
        public string Name { get; } = name;
        public string Type { get; } = type;
        public uint InterfaceIndex { get; } = index;
        public bool IsResolve => resolve is not null;
        public bool IsSettled { get; private set; }
        public bool CancelRequested { get; private set; }
        public bool SettleOnNonPendingStart = true;
        public uint StartStatus = 9506;
        public uint CancelStatus;
        public int CancelCalls;
        public int StartCalls;
        public int ReleaseCalls;
        public TaskCompletionSource StartReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? DuringStart;
        public Action? DuringCancelRequest;
        public Action? DuringCancel;
        public bool DeferCancellationUntilCallbacksReturn;
        public Task DeferredCancellation { get; private set; } = Task.CompletedTask;
        public event Action? Settled;
        public event Action<string>? CancellationFailed;
        public event Action<string>? CallbackFailed { add { } remove { } }
        public int CancellationFailureSubscriberCount => CancellationFailed?.GetInvocationList().Length ?? 0;
        public void EmitCancellationFailure(string message) => CancellationFailed?.Invoke(message);
        public uint Start()
        {
            StartCalls++;
            try
            {
                DuringStart?.Invoke();
                if (StartStatus != 9506 && SettleOnNonPendingStart) Settle();
                return StartStatus;
            }
            finally { StartReturned.TrySetResult(); }
        }
        public uint Cancel()
        {
            CancelCalls++;
            CancelRequested = true;
            DuringCancelRequest?.Invoke();
            lock (_callbackGate)
            {
                if (DeferCancellationUntilCallbacksReturn && _activeCallbacks != 0)
                {
                    _cancelDeferred = true;
                    return 0;
                }
            }
            return CompleteCancellation();
        }
        private uint CompleteCancellation()
        {
            DuringCancel?.Invoke();
            if (owner.SettleOnCancel) Settle();
            return CancelStatus;
        }
        public void Announced(uint status, params DnsAnnouncement[] announcements)
        {
            lock (_callbackGate) _activeCallbacks++;
            try { browse!(status, announcements); }
            finally { ExitCallback(); }
        }
        public void Resolved(uint status, ServiceRecord? record)
        {
            lock (_callbackGate) _activeCallbacks++;
            try { resolve!(status, record); }
            finally { ExitCallback(); }
        }
        private void ExitCallback()
        {
            lock (_callbackGate)
            {
                if (--_activeCallbacks != 0 || !_cancelDeferred) return;
                _cancelDeferred = false;
                DeferredCancellation = Task.Run(CompleteCancellation);
            }
        }
        public void Settle()
        {
            if (IsSettled) return;
            IsSettled = true;
            ReleaseCalls++;
            owner.Releases++;
            Settled?.Invoke();
        }
    }
}
