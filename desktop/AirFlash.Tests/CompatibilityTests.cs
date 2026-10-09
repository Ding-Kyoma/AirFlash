using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void DiscoveryPreservesDistinctServicePortsAndCapabilities()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([
            new("Office._airplay._tcp.local", "_airplay._tcp.local", "192.0.2.1", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:FF", ["features"] = "0x1,0x102" }),
            new("AABBCCDDEEFF@Office._raop._tcp.local", "_raop._tcp.local", "192.0.2.1", 5000,
                new Dictionary<string, string> { ["cn"] = "1", ["sr"] = "44100", ["pw"] = "false" })]));
        Assert.Equal(2, receiver.Services.Length);
        Assert.Equal(new[] { 7000, 5000 }, receiver.Services.Select(s => s.Port));
        Assert.Equal("0x1,0x102", receiver.Services[0].Txt["features"]);
        Assert.Equal(new byte[] { 1 }, receiver.Codecs);
        Assert.Contains("192.0.2.1:5000", receiver.Aliases);
    }

    [Fact]
    public void OverridesRoundTripAndSurviveIdentityMigration()
    {
        var settings = new AppSettings();
        var options = settings.Options("AABBCCDDEEFF");
        options.TransportMode = "buffered"; options.TimingMode = "ptp";
        options.LatencyMode = "custom"; options.CustomBufferMs = 6000;
        var clone = settings.Clone();
        ReceiverCatalog.ApplyMigrations(clone, new Dictionary<string, string> { ["AABBCCDDEEFF"] = "aabbccddeeff" });
        Assert.Equal("buffered", clone.ReadOptions("aabbccddeeff").TransportMode);
        Assert.Equal("ptp", clone.ReadOptions("aabbccddeeff").TimingMode);
        Assert.Equal(6000, clone.Latency("aabbccddeeff"));
        Assert.Null(clone.Validate());
        clone.Options("aabbccddeeff").CustomBufferMs = 10001;
        Assert.NotNull(clone.Validate());
    }

    [Fact]
    public void OnlyNegotiationMetadataChangesThePlaybackSignature()
    {
        Receiver Device(string flags, string name) => new("speaker", "Office", "192.0.2.1")
        {
            Services = [new("Office._airplay._tcp.local", "_airplay._tcp.local", "192.0.2.1", 7000,
                new Dictionary<string, string> { ["sf"] = flags, ["gpn"] = name })]
        };
        var before = Device("0", "Office");
        Assert.Equal(before.TransportKey, Device("0", "Renamed").TransportKey);
        Assert.NotEqual(before.TransportKey, Device("8", "Office").TransportKey);
        Assert.Equal("Office", before.Services[0].Txt["gpn"]);
    }

    [Fact]
    public void OldSettingsSelectAutomaticWithNoCompatibilityOverride()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"schema_version\":2,\"receivers\":{\"speaker\":{\"volume\":35}}}", AppSettings.JsonOptions)!;
        Assert.Null(settings.ReadOptions("speaker").TransportMode);
        Assert.Null(settings.ReadOptions("speaker").TimingMode);
        Assert.Null(settings.CompatibilityBuffer("speaker"));
        settings.Options("speaker").LatencyMode = "realtime";
        Assert.Equal(120, settings.CompatibilityBuffer("speaker"));
    }
}
