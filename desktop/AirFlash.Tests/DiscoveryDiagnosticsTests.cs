using System.Text.Json;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class DiscoveryDiagnosticsTests
{
    private static readonly DateTimeOffset Receipt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private const string Airplay = "_airplay._tcp.local", Raop = "_raop._tcp.local";
    private static ServiceRecord Record(string type = Airplay, int port = 7000) => new(
        type == Airplay ? "PrivateFixture." + type : "AABBCCDDEE01@PrivateFixture." + type,
        type, "192.0.2.123", port, new Dictionary<string, string>
        {
            ["deviceid"] = "AA:BB:CC:DD:EE:01", ["tsid"] = "aabbccddee01", ["ignored"] = "NeverExportCredential"
        });
    private static void Add(DiscoveryHistory history, ServiceRecord? record = null, long epoch = 1, bool conflict = false)
        => history.Record(Receipt, TimeSpan.FromSeconds(9), epoch, DiscoveryTraceEvent.ResolveSucceeded,
            DiscoveryTraceReason.None, DiscoveryDemand.Panel, 1, record: record ?? Record(), identityConflict: conflict);
    private static DiscoveryDiagnosticsSnapshot Snapshot(DiscoveryHistory history)
        => new(1, DiscoveryDemand.Panel, true, true, true, 1, 1, 0, 2, history.Recorded, history.Overwritten, history.Snapshot());

    [Fact]
    public void History_is_bounded_ordered_and_previous_snapshots_are_independent()
    {
        var history = new DiscoveryHistory(); Add(history);
        var first = history.Snapshot();
        for (var i = 0; i < DiscoveryDiagnosticsSnapshot.HistoryCapacity + 7; i++) Add(history);
        var snapshot = Snapshot(history);
        Assert.Equal(DiscoveryDiagnosticsSnapshot.HistoryCapacity, snapshot.History.Count);
        Assert.Equal(136, snapshot.RecordedEvents); Assert.Equal(8, snapshot.OverwrittenEvents);
        Assert.Equal(Enumerable.Range(9, 128).Select(value => (long)value), snapshot.History.Select(entry => entry.Sequence));
        Assert.Single(first); Assert.Equal(1, first[0].Sequence);
        Assert.All(snapshot.History, entry => { Assert.Equal(Receipt, entry.ReceivedAt); Assert.Equal(9000, entry.ElapsedMs); });
    }

    [Fact]
    public void Normalized_device_and_endpoint_correlations_survive_service_changes_and_epochs()
    {
        var history = new DiscoveryHistory();
        var airplay = Record(); Add(history, airplay);
        Add(history, airplay with { Instance = airplay.Instance.ToUpperInvariant() + ".", ServiceType = Airplay.ToUpperInvariant() + "." }, epoch: 2);
        Add(history, Record(Raop) with { Txt = new Dictionary<string, string>() }, epoch: 3);
        Add(history, Record(port: 7001));
        var entries = history.Snapshot();
        Assert.Equal(entries[0].ServiceToken, entries[1].ServiceToken);
        Assert.Equal(DiscoveryServiceType.Airplay, entries[1].ServiceType);
        Assert.Equal(entries[0].DeviceToken, entries[2].DeviceToken);
        Assert.Equal(entries[0].EndpointToken, entries[2].EndpointToken);
        Assert.NotEqual(entries[0].EndpointToken, entries[3].EndpointToken);
        Assert.NotEqual(entries[0].DeviceToken, entries[0].StereoToken);
        Assert.False(entries[2].StereoMetadataPresent); Assert.Null(entries[2].StereoToken);
        Assert.NotEqual(entries[0].ServiceToken, entries[2].ServiceToken);
    }

    [Fact]
    public void Unknown_or_conflicting_identity_is_not_exported_as_a_device_correlation()
    {
        var history = new DiscoveryHistory();
        Add(history, Record(), conflict: true);
        Add(history, Record() with { Txt = new Dictionary<string, string>() });
        Assert.All(history.Snapshot(), entry => Assert.Null(entry.DeviceToken));
    }

    [Fact]
    public void Metadata_and_endpoint_normalization_match_catalog_semantics()
    {
        var history = new DiscoveryHistory();
        Add(history, Record() with { Address = "  FIXTURE.local  ", Txt = new Dictionary<string, string> { ["tsid"] = "  fixture-pair  " } });
        Add(history, Record() with { Address = "fixture.local", Txt = new Dictionary<string, string> { ["tsid"] = "fixture-pair" } });
        Add(history, Record() with { Txt = new Dictionary<string, string> { ["tsid"] = "  " } });
        var entries = history.Snapshot();
        Assert.Equal(entries[0].EndpointToken, entries[1].EndpointToken);
        Assert.Equal(entries[0].StereoToken, entries[1].StereoToken);
        Assert.False(entries[2].StereoMetadataPresent); Assert.Null(entries[2].StereoToken);
    }

    [Fact]
    public void Separate_discovery_instances_have_unlinkable_tokens_and_no_raw_fields_are_exported()
    {
        var first = new DiscoveryHistory(); var second = new DiscoveryHistory(); Add(first); Add(second);
        Assert.NotEqual(first.Snapshot()[0].ServiceToken, second.Snapshot()[0].ServiceToken);
        var text = DiagnosticExport.Compose("test", "test", null, discovery: Snapshot(first));
        foreach (var secret in new[] { "PrivateFixture", "192.0.2.123", "AA:BB:CC:DD:EE:01", "aabbccddee01", "NeverExportCredential" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        using var bundle = JsonDocument.Parse(text);
        Assert.False(bundle.RootElement.TryGetProperty("session", out _));
        var discovery = bundle.RootElement.GetProperty("discovery");
        Assert.Equal(1, discovery.Integer("recorded_events"));
        var entry = Assert.Single(discovery.GetProperty("history").EnumerateArray());
        Assert.Equal("ResolveSucceeded", entry.Text("event"));
        Assert.Equal(64, entry.Text("service_token").Length);
        Assert.True(entry.GetProperty("stereo_metadata_present").GetBoolean());
    }

    [Fact]
    public void Export_rejects_malformed_provider_tokens_unknown_events_and_unknown_demand_bits()
    {
        var history = new DiscoveryHistory(); Add(history);
        var privateText = new string('z', 64);
        var entry = history.Snapshot()[0] with
        {
            ServiceToken = privateText, DeviceToken = "192.0.2.123", StereoToken = "PrivateStereo", EndpointToken = "C:/Private",
            Demand = (DiscoveryDemand)127
        };
        var snapshot = Snapshot(history) with { Demand = (DiscoveryDemand)127,
            History = [entry, entry with { Event = (DiscoveryTraceEvent)999 }] };
        var text = DiagnosticExport.Compose("test", "test", null, discovery: snapshot);
        using var bundle = JsonDocument.Parse(text);
        var discovery = bundle.RootElement.GetProperty("discovery");
        Assert.Equal("Panel, Receivers, Startup, SoftwareStereo", discovery.Text("demand"));
        var saved = Assert.Single(discovery.GetProperty("history").EnumerateArray());
        Assert.Equal("Panel, Receivers, Startup, SoftwareStereo", saved.Text("demand"));
        foreach (var property in new[] { "service_token", "device_token", "stereo_token", "endpoint_token" })
            Assert.Equal(JsonValueKind.Null, saved.GetProperty(property).ValueKind);
        foreach (var secret in new[] { privateText, "192.0.2.123", "PrivateStereo", "C:/Private" }) Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void Export_bounds_even_an_oversized_optional_provider_snapshot()
    {
        var history = new DiscoveryHistory(); Add(history);
        var entry = history.Snapshot()[0];
        var snapshot = Snapshot(history) with { History = Enumerable.Range(1, 200).Select(index => entry with { Sequence = index }).ToArray() };
        using var bundle = JsonDocument.Parse(DiagnosticExport.Compose("test", "test", null, discovery: snapshot));
        var saved = bundle.RootElement.GetProperty("discovery").GetProperty("history");
        Assert.Equal(128, saved.GetArrayLength()); Assert.Equal(73, saved[0].Integer("sequence"));
    }
}
