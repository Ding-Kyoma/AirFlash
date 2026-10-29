using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class AirPlayCapabilityTests
{
    private const ulong Bit40 = 1UL << 40;

    public static IEnumerable<object?[]> PredicateCases()
    {
        // features, srcvers, expected
        yield return [null, null, false];
        yield return [null, "366.0", false];
        yield return [0UL, null, false];
        yield return [0UL, "366.0", false];
        yield return [0x4A7FCA00UL, "366.0", false];
        yield return [Bit40, null, false];
        yield return [Bit40, "", false];
        yield return [Bit40, "220.68", false];
        yield return [Bit40, "354.54.5", false];
        yield return [Bit40, "354.54.6", true];
        yield return [Bit40, "354.54.7", true];
        yield return [Bit40, "366.0", true];
        yield return [Bit40 | (1UL << 41), "366.0", true];
        yield return [Bit40 | (1UL << 17), "354.54.6", true];
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void SupportsAirPlay2FollowsWacBit40AndSrcversPredicate(ulong? features, string? sourceVersion, bool expected)
        => Assert.Equal(expected, AirPlayCapability.SupportsAirPlay2(features, sourceVersion));

    [Theory]
    [InlineData("354.54.6", true)]
    [InlineData("354.54.5", false)]
    [InlineData("354.54.7", true)]
    [InlineData("366.0", true)]
    [InlineData("220.68", false)]
    [InlineData("354.54", false)]
    [InlineData("354", false)]
    [InlineData("354.54.6.1", true)]
    [InlineData("354.54.6.0.9", true)]
    [InlineData(" 354.54.6 ", true)]
    [InlineData("355", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc", false)]
    [InlineData("354.54.x", false)]
    [InlineData("354..6", false)]
    [InlineData("354.54.6-beta", false)]
    [InlineData("-354.54.6", false)]
    public void VersionComparisonToleratesMissingAndExtraComponents(string? sourceVersion, bool expected)
        => Assert.Equal(expected, AirPlayCapability.VersionAtLeast(sourceVersion, new Version(354, 54, 6)));

    [Fact]
    public void VersionParserKeepsTheParsedComponents()
    {
        Assert.True(AirPlayCapability.TryParseVersion("354.54.6", out var three));
        Assert.Equal(new Version(354, 54, 6), three);
        Assert.True(AirPlayCapability.TryParseVersion("354.54", out var two));
        Assert.Equal(new Version(354, 54), two);
        Assert.True(AirPlayCapability.TryParseVersion("354.54.6.1.2", out var extra));
        Assert.Equal(new Version(354, 54, 6, 1), extra);
        Assert.False(AirPlayCapability.TryParseVersion("354.54.6x", out _));
        Assert.False(AirPlayCapability.TryParseVersion("354", out _));
    }

    [Theory]
    [InlineData(null, "366.0", AirPlaySupport.Unknown)]
    [InlineData(0UL, "220.68", AirPlaySupport.ClassicOnly)]
    [InlineData(0x4A7FCA00UL, "366.0", AirPlaySupport.ClassicOnly)]
    [InlineData(Bit40, "354.54.6", AirPlaySupport.AirPlay2)]
    [InlineData(Bit40, "366.0", AirPlaySupport.AirPlay2)]
    [InlineData(Bit40, "354.54.5", AirPlaySupport.ClassicOnly)]
    [InlineData(Bit40, null, AirPlaySupport.Unknown)]
    [InlineData(Bit40, "not-a-version", AirPlaySupport.Unknown)]
    public void ThreeWayEvidenceNeverTurnsMissingFeaturesIntoClassic(ulong? features, string? sourceVersion, AirPlaySupport expected)
        => Assert.Equal(expected, AirPlayCapability.Classify(features, sourceVersion));

    [Fact]
    public void AggregatorParsesSrcversAndBothFeatureWords()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([
            new ServiceRecord("HomePod._airplay._tcp.local", "_airplay._tcp.local", "10.0.0.5", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:01", ["model"] = "AudioAccessory5,1", ["features"] = "0x400,0x100", ["srcvers"] = "366.0" })
        ]));
        Assert.Equal("366.0", receiver.SourceVersion);
        Assert.Equal((0x100UL << 32) | 0x400, receiver.Features);
        Assert.True(receiver.SupportsAirPlay2);
        Assert.Equal(AirPlaySupport.AirPlay2, receiver.Support);
    }

    [Fact]
    public void AggregatorFallsBackToFtAndKeepsSrcvers()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([
            new ServiceRecord("Express._airplay._tcp.local", "_airplay._tcp.local", "10.0.0.6", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:02", ["ft"] = "0x0,0x0", ["srcvers"] = "220.68" })
        ]));
        Assert.Equal("220.68", receiver.SourceVersion);
        Assert.Equal(0UL, receiver.Features);
        Assert.False(receiver.SupportsAirPlay2);
        Assert.Equal(AirPlaySupport.ClassicOnly, receiver.Support);
    }

    [Fact]
    public void AggregatorLeavesMissingFeatureEvidenceUnknown()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([
            new ServiceRecord("Speaker._airplay._tcp.local", "_airplay._tcp.local", "10.0.0.7", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:03", ["srcvers"] = "366.0" })
        ]));
        Assert.Equal("366.0", receiver.SourceVersion);
        Assert.Null(receiver.Features);
        Assert.False(receiver.SupportsAirPlay2);
        Assert.Equal(AirPlaySupport.Unknown, receiver.Support);
    }

    [Fact]
    public void AggregatorLeavesBit40WithoutSrcversUnknown()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([
            new ServiceRecord("Speaker._airplay._tcp.local", "_airplay._tcp.local", "10.0.0.8", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:04", ["features"] = "0x400,0x100" })
        ]));
        Assert.Equal("", receiver.SourceVersion);
        Assert.False(receiver.SupportsAirPlay2);
        Assert.Equal(AirPlaySupport.Unknown, receiver.Support);
    }

    [Fact]
    public void DetailAnnotatesAirPlay2AndClassicOnlyWithoutChangingGroups()
    {
        var airplay2 = new Receiver("homepod", "HomePod", "10.0.0.5") { Model = "AudioAccessory5,1", Features = Bit40, SourceVersion = "366.0" };
        var classic = new Receiver("express", "AirPort Express", "10.0.0.6") { Model = "AirPort10,115", Features = 0UL, SourceVersion = "220.68" };
        var unknown = new Receiver("manual", "Manual", "10.0.0.7") { Model = "Unknown model" };
        Assert.Equal($"AudioAccessory5,1 · {L.Get("AirPlay 2")}", airplay2.Detail);
        Assert.Equal($"AirPort10,115 · {L.Get("Classic AirPlay only")}", classic.Detail);
        Assert.Equal($"Unknown model · {L.Get("AirPlay 2 support unknown")}", unknown.Detail);
        Assert.DoesNotContain(L.Get("Classic AirPlay only"), airplay2.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(L.Get("Classic AirPlay only"), unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailNeverAnnotatesStereoGroupsOrSoftwareGroups()
    {
        var classic = new Receiver("a", "Left", "10.0.0.1") { Features = 0UL, SourceVersion = "220.68" };
        var modern = new Receiver("b", "Right", "10.0.0.2") { Features = Bit40, SourceVersion = "366.0" };
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [classic, modern] };
        var software = new Receiver("software:pair", "Software pair", "") { IsSoftwareGroup = true, Members = [classic, modern] };
        Assert.Equal(L.Format("HomePod stereo · {0}/2", 2), group.Detail);
        Assert.Equal(L.Get("Software stereo · experimental"), software.Detail);
        Assert.DoesNotContain(L.Get("AirPlay 2"), group.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(L.Get("Classic AirPlay only"), group.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(L.Get("AirPlay 2 support unknown"), group.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSingleKeepsItsModelAndNeverClaimsClassic()
    {
        var receiver = new Receiver("unknown", "Unknown", "10.0.0.9") { Model = "AirFlash receiver" };
        Assert.Equal($"AirFlash receiver · {L.Get("AirPlay 2 support unknown")}", receiver.Detail);
        Assert.DoesNotContain(L.Get("Classic AirPlay only"), receiver.Detail, StringComparison.Ordinal);
    }
}
