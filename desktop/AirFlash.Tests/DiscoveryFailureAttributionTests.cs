using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class DiscoveryFailureAttributionTests
{
    private static readonly Receiver Active = Peer("001122334455", "192.0.2.1");
    private static readonly Receiver Other = Peer("66778899aabb", "192.0.2.2");
    private static readonly IReadOnlyDictionary<string, string> NoAliases = new Dictionary<string, string>();

    [Fact]
    public void OnlyUniqueOnlineIdentityAndEndpointProveAnotherReceiver()
    {
        Assert.True(IsOther(Context(Other), Active, Active, Other));
        Assert.False(IsOther(Context(Other), Active, Active, Other with { Online = false }));
        Assert.False(IsOther(Context(Other), Active, Active, Other, Peer("ccddee000011", Other.Address)));
        Assert.False(IsOther(Context(Other) with { DeviceId = "" }, Active, Active, Other));
        Assert.False(IsOther(Context(Other) with { Port = 7001 }, Active, Active, Other));
        Assert.False(IsOther(Context(Other), Active with { DeviceId = "", IsManual = true }, Active, Other));
    }

    [Fact]
    public void ActiveIdentityAliasesAndSharedHostsStayVisible()
    {
        Assert.False(IsOther(Context(Other), Active with { Aliases = ["66:77:88:99:AA:BB"] }, Active, Other));
        Assert.False(IsOther(Context(Other), Active with { Address = Other.Address, Port = 7100 }, Active, Other));
        var aliases = new Dictionary<string, string> { [Active.Id] = Other.Id };
        Assert.False(DiscoveryFailureAttribution.IsOtherReceiver(Context(Other), Active, [Active, Other], aliases));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryActiveStereoMemberRemainsRelevant(bool software)
    {
        var group = Active with { Id = software ? "software:pair" : "stereo:pair", IsSoftwareGroup = software, Members = [Active, Other] };
        Assert.False(IsOther(Context(Other), group, Active, Other));
        Assert.False(IsOther(Context(Active), group, Active, Other));
        var unrelated = Peer("ccddee000011", "192.0.2.3");
        Assert.True(IsOther(Context(unrelated), group, Active, Other, unrelated));
        Assert.False(IsOther(Context(unrelated) with { StereoId = "pair" }, group with { StereoId = "pair" }, Active, Other, unrelated));
    }

    private static Receiver Peer(string id, string address) => new(id, id, address) { DeviceId = id };
    private static DiscoveryResolveContext Context(Receiver receiver) => new(receiver.DeviceId, receiver.Address, receiver.Port, receiver.StereoId);
    private static bool IsOther(DiscoveryResolveContext source, Receiver active, params Receiver[] catalog)
        => DiscoveryFailureAttribution.IsOtherReceiver(source, active, catalog, NoAliases);
}
