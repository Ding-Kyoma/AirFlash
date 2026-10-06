using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiscoveryTests
{
    private static ServiceRecord Member(string name, string address, string id, bool leader, string kind = "_airplay._tcp.local") => new(name, kind, address, 7000, new Dictionary<string, string> { ["deviceid"] = id, ["tsid"] = "stable", ["gid"] = "unstable", ["gpn"] = "家庭影院", ["igl"] = leader ? "1" : "0", ["cn"] = "0,1,2,3" });
    [Fact]
    public void SelectedInterfaceNeverFallsBackToAllWhenMissingOrDisconnected()
    {
        const string selected = "01db7c77-1016-49cb-8f5c-f5c194425d15";
        var adapters = new[] { new DiscoveryAdapter(selected, "Wi-Fi", "Physical", "192.168.1.2", true, 17),
            new DiscoveryAdapter("vpn", "ZeroTier", "Virtual", "10.1.1.2", true, 42) };
        Assert.Equal(0u, DiscoveryInterface.ResolveIndex("", adapters));
        Assert.Equal(17u, DiscoveryInterface.ResolveIndex(selected, adapters));
        Assert.Null(DiscoveryInterface.ResolveIndex("missing", adapters));
        Assert.Null(DiscoveryInterface.ResolveIndex(selected, adapters.Select(a => a with { IsUp = false })));
        Assert.Null(DiscoveryInterface.ResolveIndex(selected, adapters.Select(a => a with { Ipv4Addresses = "" })));
    }
    [Fact]
    public void AggregatesBothServicesAndPreservesCodecList()
    {
        var receivers = ReceiverAggregator.Build([Member("left._airplay._tcp.local", "10.0.0.1", "aa:bb:cc:dd:ee:01", true), Member("right._airplay._tcp.local", "10.0.0.2", "aa:bb:cc:dd:ee:02", false), Member("aabbccddee01@Cinema._raop._tcp.local", "10.0.0.1", "aabbccddee01", true, "_raop._tcp.local")]);
        var pair = Assert.Single(receivers); Assert.Equal("stereo:stable", pair.Id); Assert.Equal("家庭影院", pair.Name); Assert.Equal(2, pair.Members.Length); Assert.True(pair.Complete); Assert.Equal(new byte[] { 0, 1, 2, 3 }, pair.Members[0].Codecs);
    }
    [Fact]
    public void ChangingGroupLeaderKeepsStableIdentity()
    {
        var pair = Assert.Single(ReceiverAggregator.Build([Member("a", "10.0.0.1", "aa", false), Member("b", "10.0.0.2", "bb", true)]));
        Assert.Equal("stereo:stable", pair.Id); Assert.Equal("10.0.0.2", pair.Address);
        var incomplete = Assert.Single(ReceiverAggregator.Build([Member("a", "10.0.0.1", "aa", false)])); Assert.False(incomplete.Complete);
    }
    [Fact]
    public void RaopInstanceIdentifierDoesNotBecomeDisplayName()
    {
        var service = new ServiceRecord("AABBCC@Cinema._raop._tcp.local", "_raop._tcp.local", "10.0.0.1", 7000, new Dictionary<string, string> { ["cn"] = "0,1,2,3" });
        var receiver = Assert.Single(ReceiverAggregator.Build([service])); Assert.Equal("Cinema", receiver.Name); Assert.Equal("AABBCC", receiver.DeviceId);
    }
}
