using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class GroupMembershipTests
{
    private const ulong Bit40 = 1UL << 40;

    private static Receiver Physical(string id, string name, ulong? features = null, string sourceVersion = "", string address = "10.0.0.5")
        => new(id, name, address) { Features = features, SourceVersion = sourceVersion };
    private static Receiver Classic(string name = "AirPort Express") => Physical("classic", name, 0UL, "220.68");
    private static Receiver AirPlay2(string name = "HomePod") => Physical("ap2", name, Bit40, "366.0");
    private static Receiver Outdated(string name = "Old HomePod") => Physical("outdated", name, Bit40, "354.54.5");
    private static Receiver Unknown(string name = "Manual receiver") => Physical("unknown", name);
    private static string Action => L.Get(MixedGroupMessage.ActionKey);

    [Fact]
    public void SingleReceiverIsAlwaysAccepted()
    {
        // WinAirCast's `Members.Count <= 0 ||` short-circuit shape: the first
        // member never meets a comparison, so a lone classic receiver is fine.
        foreach (var receiver in new[] { Classic(), AirPlay2(), Outdated(), Unknown() })
        {
            var membership = new GroupMembership();
            Assert.True(membership.TryAdd(receiver, out var error));
            Assert.Null(error);
            Assert.Equal(new[] { receiver }, membership.Members);
        }
    }

    [Fact]
    public void AddingClassicReceiverToAirPlay2SetFailsAndLeavesMembersUnchanged()
    {
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(AirPlay2(), out _));
        var before = membership.Members.ToArray();

        Assert.False(membership.TryAdd(Classic(), out var error));

        Assert.Equal(Action, error);
        Assert.Equal(before, membership.Members);
        Assert.Equal(new[] { "ap2" }, membership.Members.Select(member => member.Id));
    }

    [Fact]
    public void AddingAirPlay2ReceiverToClassicSetFailsSymmetrically()
    {
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(Classic(), out _));
        var before = membership.Members.ToArray();

        Assert.False(membership.TryAdd(AirPlay2(), out var error));

        Assert.Equal(Action, error);
        Assert.Equal(before, membership.Members);
        Assert.Equal(new[] { "classic" }, membership.Members.Select(member => member.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddingSameKindReceiverSucceeds(bool airPlay2)
    {
        var membership = new GroupMembership();
        var first = airPlay2 ? AirPlay2("First") : Classic("First");
        var second = airPlay2 ? AirPlay2("Second") : Classic("Second");
        Assert.True(membership.TryAdd(first, out _));
        Assert.True(membership.TryAdd(second, out var error));
        Assert.Null(error);
        Assert.Equal(new[] { first, second }, membership.Members);
    }

    [Fact]
    public void OutdatedClaimAppendsReasonWithoutReplacingAction()
    {
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(AirPlay2(), out _));
        var outdated = Outdated("客厅 Apple TV");

        Assert.False(membership.TryAdd(outdated, out var error));

        Assert.StartsWith(Action, error);
        Assert.NotEqual(Action, error);
        Assert.Contains(outdated.Name, error);
        Assert.Contains(L.Format(MixedGroupMessage.OutdatedReasonKey, outdated.Name), error);
        Assert.Single(membership.Members);
    }

    [Fact]
    public void OutdatedReceiverAlreadyInTheSetIsNamedWhenAirPlay2IsAdded()
    {
        var membership = new GroupMembership();
        var outdated = Outdated("Old HomePod");
        Assert.True(membership.TryAdd(outdated, out _));

        Assert.False(membership.TryAdd(AirPlay2("New HomePod"), out var error));

        Assert.StartsWith(Action, error);
        Assert.Contains(outdated.Name, error);
        Assert.Single(membership.Members);
    }

    [Fact]
    public void ClassicNeverAppendsAReason()
    {
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(AirPlay2(), out _));
        Assert.False(membership.TryAdd(Classic(), out var error));
        Assert.Equal(Action, error);
    }

    [Fact]
    public void UnknownCapabilityReceiverIsAcceptedNotRejected()
    {
        // Q45: absence of evidence is not evidence of absence. An offline/unknown
        // receiver may join; the contradiction is caught by Resolve once the TXT
        // record arrives.
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(AirPlay2("Known"), out _));
        var unknown = Unknown("Bedroom HomePod");
        Assert.True(membership.TryAdd(unknown, out var error));
        Assert.Null(error);
        Assert.Equal(new[] { "ap2", "unknown" }, membership.Members.Select(member => member.Id));
        Assert.False(GroupMembership.HasProvenConflict(membership.Members));
    }

    [Fact]
    public void OutdatedClaimAndClassicAreBothRoutingFalseSoTheyCanGroup()
    {
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(Outdated(), out _));
        Assert.True(membership.TryAdd(Classic("AirPort Express"), out var error));
        Assert.Null(error);
        Assert.Equal(2, membership.Count);
    }

    [Fact]
    public void ResolveCatchesContradictionOnlyWhenBothSidesHaveEvidence()
    {
        var settings = new AppSettings();
        settings.SoftwareGroups.Add(new() { LeftId = "ap2", RightId = "unknown" });
        // While the second receiver is unknown the saved pair is representable.
        var before = SoftwareStereo.Resolve([AirPlay2(), Unknown() with { Address = "10.0.0.6" }], settings).Single();
        Assert.True(before.Online);
        Assert.Equal(2, before.Members.Length);
        // Once its TXT proves classic, Resolve refuses without touching the config.
        var after = SoftwareStereo.Resolve([AirPlay2(), Physical("unknown", "Bedroom", 0UL, "220.68", "10.0.0.6")], settings).Single();
        Assert.False(after.Online);
        Assert.Empty(after.Members);
        Assert.Single(settings.SoftwareGroups);
    }

    [Fact]
    public void SoftwareStereoPairIsNotExemptFromTheInvariant()
    {
        // Q41 asked whether to copy WinAirCast's `!IsCustomStereoPair` bypass.
        // AirFlash's software stereo group is two independent receivers sent as
        // `software_stereo` peers, not a `SetStereoPair` logical unit, and the
        // engine refuses a mixed pair; so the pair gets no exemption.
        var membership = new GroupMembership();
        Assert.True(membership.TryAdd(AirPlay2("Left"), out _));

        Assert.False(membership.TryAdd(Classic("Right"), out var error));

        Assert.Equal(Action, error);
        Assert.Single(membership.Members);
    }
}
