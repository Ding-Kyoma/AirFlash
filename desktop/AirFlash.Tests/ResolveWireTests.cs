using System.Text.Json;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class ResolveWireTests
{
    private const string Type = "_raop._tcp.local", Name = "Synthetic._raop._tcp.local";
    private static readonly ResolveRequest Request = new(Name, Type, 42);

    [Theory]
    [InlineData("FeAtUrEs", "27.0", "", true)]
    [InlineData("Ft", "27.0", "", true)]
    [InlineData("FeAtUrEs", "26.0", "", false)]
    [InlineData("Ft", "27.0", "synthetic-native-group", false)]
    public void CopiedMixedCaseProfileTxtStillDrivesCurrentReceiverConsumers(string featureKey, string osVersion, string groupId, bool followsClock)
    {
        var original = new ServiceRecord(Name, Type, "192.0.2.1", 7000, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeViCeId"] = "synthetic-profile", ["MoDeL"] = "AudioAccessory5,1", ["IgL"] = "1",
            [featureKey] = "0x1,0x200", ["Pw"] = "TrUe", ["OsVeRs"] = osVersion, ["PgId"] = groupId
        });
        var copied = Assert.IsType<ServiceRecord>(ResolveWire.Parse(JsonSerializer.Serialize(new ResolveReply(1, 0, original)), Request).Record);
        Assert.Equal("TrUe", copied.Txt["pw"]);
        Assert.Equal(osVersion, copied.Txt["osvers"]);
        Assert.Equal(groupId, copied.Txt["pgid"]);
        Assert.Equal("0x1,0x200", copied.Txt[featureKey.ToLowerInvariant()]);
        var receiver = Assert.Single(ReceiverAggregator.Build([copied]));
        Assert.Equal((1ul << 41) | 1, receiver.Features);
        Assert.True(receiver.RequiresPassword);
        Assert.Equal(followsClock, receiver.FollowReceiverClock);
    }

    [Fact]
    public void SerializedTxtPreservesNativeCaseInsensitiveKeysAndOrdinalValues()
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeViCeId"] = "synthetic-device", ["TsId"] = "synthetic-group",
            ["GpN"] = "Synthetic room", ["IgL"] = "1", ["Cn"] = "0,1", ["Pk"] = "Key"
        };
        var original = new ServiceRecord(Name, Type, "192.0.2.1", 7000, txt);
        var wire = JsonSerializer.Serialize(new ResolveReply(1, 0, original));
        var copied = Assert.IsType<ServiceRecord>(ResolveWire.Parse(wire, Request).Record);
        Assert.Equal("synthetic-device", copied.Txt.GetValueOrDefault("deviceid"));
        Assert.Equal("Key", copied.Txt.GetValueOrDefault("pk"));
        var group = Assert.Single(ReceiverAggregator.Build([copied]));
        Assert.Equal("stereo:synthetic-group", group.Id);
        Assert.Equal("Synthetic room", group.Name);
        var member = Assert.Single(group.Members);
        Assert.Equal("synthetic-device", member.Id);
        Assert.Equal("synthetic-device", member.DeviceId);
        Assert.True(member.IsLeader);
        Assert.Equal(new byte[] { 0, 1 }, member.Codecs);
        var conflicting = copied with
        {
            Instance = "Other._raop._tcp.local",
            Txt = new Dictionary<string, string> { ["deviceid"] = "synthetic-other", ["pk"] = "key" }
        };
        Assert.Equal(2, ReceiverAggregator.Build([copied, conflicting]).Count);
    }

    [Fact]
    public void CaseFoldedDuplicateTxtKeysAreRejectedAsAnInvalidReply()
    {
        var record = new ServiceRecord(Name, Type, "192.0.2.1", 7000,
            new Dictionary<string, string> { ["deviceid"] = "synthetic-a", ["DEVICEID"] = "synthetic-b" });
        var wire = JsonSerializer.Serialize(new ResolveReply(1, 0, record));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.Parse(wire, Request));
    }
}
