using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class ReceiverSelectionTests
{
    private static Receiver Member(string id = "member-a", string address = "192.0.2.10", int port = 7000)
        => new(id, id, address, port);
    private static Receiver Group(string id, params Receiver[] members)
        => new("stereo:" + id, id, members.FirstOrDefault()?.Address ?? "") { Members = members };
    private static HashSet<string> Attempted(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Theory]
    [InlineData(7000, true)]
    [InlineData(7001, true)]
    [InlineData(7000, false)]
    [InlineData(7001, false)]
    public void ManualSelectionPreservesItsExactEndpointAndIdentity(int port, bool complete)
    {
        var manual = Member("manual", port: port) with { IsManual = true, Aliases = ["member-a"] };
        var group = Group("pair", complete ? [Member(), Member("member-b", "192.0.2.11")] : [Member()]);
        Assert.Same(manual, ReceiverSelection.ResolvePlayback(manual, [group, manual]));
        Assert.Equal(port, manual.Port);
    }

    [Fact]
    public void AlreadySelectedGroupNeverRedirectsToAnotherGroupAtItsAddress()
    {
        var selected = Group("selected", Member(), Member("member-b", "192.0.2.11"));
        var other = Group("other", Member("other-member"));
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [other, selected]));
    }

    [Fact]
    public void CanonicalMemberResolvesItsUniqueGroup()
    {
        var selected = Member();
        var group = Group("pair", selected, Member("member-b", "192.0.2.11"));
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [selected, group]));
    }

    [Fact]
    public void ConfirmedBroadcastAliasResolvesItsUniqueGroupAfterAddressChange()
    {
        var selected = Member("old-broadcast", "192.0.2.50");
        var group = Group("pair", Member("canonical") with { Aliases = [selected.Id] }, Member("member-b", "192.0.2.11"));
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01", "aabbccddee01", false)]
    [InlineData("AA-BB-CC-DD-EE-01", "aabbccddee01", true)]
    public void MacIdentityComparisonUsesCatalogNormalization(string selectedId, string memberId, bool alias)
    {
        var selected = Member(selectedId);
        var member = alias ? Member("canonical") with { Aliases = [memberId] } : Member(memberId);
        var group = Group("pair", member);
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void NonMacBroadcastAliasesRemainCaseSensitive()
    {
        var selected = Member("Case-Sensitive-ID");
        var group = Group("pair", Member("canonical") with { Aliases = ["case-sensitive-id"] });
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Theory]
    [InlineData(7000)]
    [InlineData(7001)]
    public void DifferingBroadcastIdentityNeverRoutesBySharedAddress(int port)
    {
        var selected = Member("different", port: port);
        var group = Group("pair", Member(), Member("member-b", "192.0.2.11"));
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void EndpointAliasDoesNotReplaceConfirmedBroadcastIdentity()
    {
        var selected = Member("different");
        var group = Group("pair", Member() with { Aliases = [ReceiverIdentity.Endpoint(selected.Address, selected.Port)] });
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void EndpointOnlySelectionHasNoAddressFallbackToAnUnconfirmedGroup()
    {
        var selected = Member("192.0.2.10:7000");
        var group = Group("pair", Member() with { Aliases = [selected.Id] });
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void EndpointCanonicalMemberCanResolveWithoutAddressFallback()
    {
        var selected = Member("192.0.2.10:7000");
        var group = Group("pair", selected);
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void AmbiguousGroupOwnersRejectInsteadOfSelectingFirst()
    {
        var selected = Member();
        var first = Group("first", selected);
        var second = Group("second", selected);
        Assert.Null(ReceiverSelection.ResolvePlayback(selected, [first, second]));
        Assert.Null(ReceiverSelection.ResolvePlayback(selected, [second, first]));
    }

    [Fact]
    public void AmbiguousBroadcastAliasRejectsDistinctPhysicalOwners()
    {
        var selected = Member("shared-alias");
        var first = Group("first", Member("first-member") with { Aliases = [selected.Id] });
        var second = Group("second", Member("second-member") with { Aliases = [selected.Id] });
        Assert.Null(ReceiverSelection.ResolvePlayback(selected, [first, second]));
    }

    [Fact]
    public void StandaloneAliasOwnerAlsoMakesGroupOwnershipAmbiguous()
    {
        var selected = Member("shared-alias");
        var group = Group("pair", Member("group-member") with { Aliases = [selected.Id] });
        var standalone = Member("standalone", "192.0.2.12") with { Aliases = [selected.Id] };
        Assert.Null(ReceiverSelection.ResolvePlayback(selected, [group, standalone]));
    }

    [Fact]
    public void CanonicalIdentityDoesNotOverrideAConflictingLiveAliasOwner()
    {
        var selected = Member();
        var group = Group("pair", selected);
        var conflicting = Member("other", "192.0.2.12") with { Aliases = [selected.Id] };
        Assert.Null(ReceiverSelection.ResolvePlayback(selected, [group, conflicting]));
    }

    [Fact]
    public void OfflineAndManualRowsDoNotClaimDiscoveredOwnership()
    {
        var selected = Member();
        var group = Group("pair", selected);
        var offline = Group("offline", selected) with { Online = false };
        var manual = Member("manual") with { IsManual = true, Aliases = [selected.Id] };
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [offline, manual, group]));
        Assert.Same(selected, ReceiverSelection.ResolvePlayback(selected, [offline, manual]));
    }

    [Fact]
    public void IncompleteConfirmedGroupRemainsTheOwnerForStartValidation()
    {
        var selected = Member();
        var group = Group("pair", selected);
        Assert.False(group.Complete);
        Assert.Same(group, ReceiverSelection.ResolvePlayback(selected, [group]));
    }

    [Fact]
    public void AttemptedPreferredReceiverDoesNotBlockAnotherCandidate()
    {
        var preferred = Member("preferred") with { Name = "Alpha" };
        var next = Member("next", "192.0.2.11") with { Name = "Beta" };
        var settings = new AppSettings { AutoConnectOnDiscover = true, LastReceiverId = preferred.Id };
        Assert.Same(next, ReceiverSelection.SelectAutoConnect([preferred, next], settings, Attempted(preferred.Id)));
        Assert.Null(ReceiverSelection.SelectAutoConnect([preferred, next], settings, Attempted(preferred.Id, next.Id)));
    }

    [Fact]
    public void LastUsedPreferenceAndNameRankingApplyAmongUnattemptedCandidates()
    {
        var alpha = Member("alpha") with { Name = "Alpha" };
        var beta = Member("beta", "192.0.2.11") with { Name = "Beta" };
        var settings = new AppSettings { AutoConnectOnDiscover = true, LastReceiverId = beta.Id };
        Assert.Same(beta, ReceiverSelection.SelectAutoConnect([alpha, beta], settings, Attempted()));
        settings.LastReceiverId = "unavailable";
        Assert.Same(alpha, ReceiverSelection.SelectAutoConnect([beta, alpha], settings, Attempted()));
        Assert.Same(beta, ReceiverSelection.SelectAutoConnect([beta, alpha], settings, Attempted(alpha.Id)));
    }

    [Fact]
    public void EqualNameCandidatesUseAStableOrdinalIdentityTieBreak()
    {
        var first = Member("a") with { Name = "Same name" };
        var second = Member("b", "192.0.2.11") with { Name = "Same name" };
        var settings = new AppSettings { AutoConnectOnDiscover = true };
        Assert.Same(first, ReceiverSelection.SelectAutoConnect([second, first], settings, Attempted()));
    }

    [Fact]
    public void AutoConnectRejectsHiddenOfflineIncompleteAndExplicitlyDisabledCandidates()
    {
        var hidden = Member("hidden");
        var offline = Member("offline") with { Online = false };
        var incomplete = Group("incomplete", Member());
        var disabled = Member("disabled");
        var valid = Member("valid");
        var settings = new AppSettings { AutoConnectOnDiscover = true, LastReceiverId = hidden.Id };
        settings.Options(hidden.Id).Hidden = true;
        settings.Options(disabled.Id).AutoConnect = false;
        foreach (var candidate in new[] { hidden, offline, incomplete, disabled })
            Assert.False(ReceiverSelection.IsAutoConnectEligible(candidate, settings, Attempted()));
        Assert.True(ReceiverSelection.IsAutoConnectEligible(valid, settings, Attempted()));
        Assert.Same(valid, ReceiverSelection.SelectAutoConnect([hidden, offline, incomplete, disabled, valid], settings, Attempted()));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void ReceiverOverrideTakesPrecedenceOverGlobalAutoConnect(bool global, bool? receiverOverride, bool expected)
    {
        var receiver = Member();
        var settings = new AppSettings { AutoConnectOnDiscover = global };
        settings.Options(receiver.Id).AutoConnect = receiverOverride;
        Assert.Equal(expected, ReceiverSelection.IsAutoConnectEligible(receiver, settings, Attempted()));
        Assert.Equal(expected, ReceiverSelection.SelectAutoConnect([receiver], settings, Attempted()) is not null);
    }

    [Fact]
    public void SelectionDoesNotMutateSettingsOrAttemptedIds()
    {
        var receiver = Member();
        var settings = new AppSettings { AutoConnectOnDiscover = true };
        var attempted = Attempted("previous");
        Assert.Same(receiver, ReceiverSelection.SelectAutoConnect([receiver], settings, attempted));
        Assert.Empty(settings.Receivers);
        Assert.Equal("previous", Assert.Single(attempted));
        Assert.Null(settings.LastReceiverId);
    }
}
