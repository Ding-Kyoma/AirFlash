using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class GateFailureTests
{
    private const ulong Bit40 = 1UL << 40;

    private static Receiver Physical(ulong? features = null, string sourceVersion = "", bool requiresPassword = false, string model = "Model")
        => new("receiver", "Receiver", "10.0.0.5") { Model = model, Features = features, SourceVersion = sourceVersion, RequiresPassword = requiresPassword };
    private static Receiver Classic() => Physical(0UL, "220.68", model: "AirPort Express");
    private static Receiver AirPlay2() => Physical(Bit40, "366.0", model: "AudioAccessory5,1");
    private static Receiver Unknown() => Physical();
    private static EngineNotice Notice(string code, string channel = "setup", string message = "synthetic failure", bool? retryable = false)
        => new(code, "10.0.0.5", channel, message, retryable);

    [Fact]
    public void ClassicSetupFailureIsReportedAsTheCapabilityGate()
    {
        var notice = Notice("protocol_error");
        Assert.Equal(GateFailureKind.ClassicOnly, GateFailure.Classify(Classic(), notice));
        Assert.Equal(L.Get("This device only supports classic AirPlay. AirFlash cannot stream to it yet."), GateFailure.Message(Classic(), notice));
    }

    [Fact]
    public void UnknownCapabilityNeverBecomesClassic()
    {
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(Unknown(), Notice("protocol_error")));
        Assert.Null(GateFailure.Message(Unknown(), Notice("protocol_error")));
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(null, Notice("protocol_error")));
    }

    [Fact]
    public void ClassicReceiversThatStopAnsweringKeepTheCapabilityExplanation()
    {
        // A classic AirPort Express can accept TCP and never answer AirPlay 2
        // /info, which reaches the desktop as a setup read timeout.
        Assert.Equal(GateFailureKind.ClassicOnly, GateFailure.Classify(Classic(), Notice("read_timeout")));
    }

    [Theory]
    [InlineData("network_error")]
    [InlineData("socket_error")]
    [InlineData("connection_reset")]
    [InlineData("connection_refused")]
    public void UnreachableClassicReceiversStillGetTheNetworkExplanation(string code)
    {
        Assert.Equal(GateFailureKind.Network, GateFailure.Classify(Classic(), Notice(code)));
    }

    [Fact]
    public void ClassicEvidenceOutsideSetupDoesNotRelabelAMidSessionFault()
    {
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(Classic(), Notice("protocol_error", channel: "feedback")));
        Assert.Equal(GateFailureKind.Network, GateFailure.Classify(Classic(), Notice("read_timeout", channel: "feedback")));
    }

    [Fact]
    public void GroupWithOneClassicMemberIsClassicLikeWac()
    {
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [Classic(), AirPlay2()] };
        Assert.Equal(GateFailureKind.ClassicOnly, GateFailure.Classify(group, Notice("protocol_error")));
    }

    [Fact]
    public void GroupOfAirPlay2MembersIsNotClassic()
    {
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [AirPlay2(), AirPlay2()] };
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(group, Notice("protocol_error")));
    }

    [Fact]
    public void MixedTimingGroupRefusalMapsToTheDedicatedGate()
    {
        // The engine refuses the group before any SETUP (QUESTIONS Q39). Even
        // though the group contains classic evidence, the authoritative code
        // wins: the advice is "connect them separately", not "replace the
        // device", and the same string is shown whatever the receiver list says.
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [Classic(), AirPlay2()] };
        var notice = Notice("mixed_timing_group", message: "AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together. Please connect them separately.");
        foreach (Receiver? receiver in new[] { group, AirPlay2(), Classic(), null })
        {
            Assert.Equal(GateFailureKind.MixedTimingGroup, GateFailure.Classify(receiver, notice));
            Assert.Equal(
                L.Get("AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together. Please connect them separately."),
                GateFailure.Message(receiver, notice));
        }
    }

    [Fact]
    public void MixedTimingGroupRefusalAppendsTheOutdatedReasonWithoutReplacingTheAction()
    {
        var outdated = Physical(Bit40, "354.54.5", model: "Old HomePod");
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [AirPlay2(), outdated] };
        var notice = Notice("mixed_timing_group", message: "synthetic engine refusal");

        var message = GateFailure.Message(group, notice);

        Assert.StartsWith(L.Get(MixedGroupMessage.ActionKey), message);
        Assert.Contains(outdated.Name, message);
        Assert.Contains(L.Format(MixedGroupMessage.OutdatedReasonKey, outdated.Name), message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationFailureIsAPairingGate(bool requiresPassword)
    {
        var receiver = Physical(Bit40, "366.0", requiresPassword);
        var notice = Notice("authentication_failed", channel: "authentication", message: "synthetic verify rejected");
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(receiver, notice));
        Assert.Equal(L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."), GateFailure.Message(receiver, notice));
        Assert.Null(GateFailure.ActionHint(notice));
    }

    [Theory]
    [InlineData("pairing rejected (TLV error [2])")]
    [InlineData("PAIRING REJECTED (TLV ERROR [2])")]
    [InlineData("pair setup failed: pairing rejected (TLV error [2]): Caused by: rejected by accessory")]
    public void OnlyTheExactTlvTwoAuthenticationFailureAddsHomeAccessGuidance(string message)
    {
        var receiver = Physical(Bit40, "366.0");
        var notice = Notice("authentication_failed", channel: "authentication", message: message);
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(receiver, notice));
        Assert.Equal(L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."), GateFailure.Message(receiver, notice));
        Assert.Equal(L.Get("In the Home app, set speaker access to “Everyone,” then try again."), GateFailure.ActionHint(notice));
    }

    [Theory]
    [InlineData("authentication_failed", "pairing rejected (TLV error [3])")]
    [InlineData("authentication_failed", "pairing rejected (TLV error [20])")]
    [InlineData("authentication_failed", "pairing authentication failed")]
    [InlineData("authentication_failed", "TLV error [2]")]
    [InlineData("authentication_failed", "MFi authentication failed")]
    [InlineData("protocol_error", "pairing rejected (TLV error [2])")]
    [InlineData("session_rejected", "pairing rejected (TLV error [2])")]
    public void OtherAuthenticationEvidenceDoesNotAddHomeAccessGuidance(string code, string message)
        => Assert.Null(GateFailure.ActionHint(Notice(code, message: message)));

    [Fact]
    public void SessionRejectionIsOnlyAPairingGateWithTxtPasswordEvidence()
    {
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(Physical(Bit40, "366.0", requiresPassword: true), Notice("session_rejected")));
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(Physical(Bit40, "366.0"), Notice("session_rejected")));
    }

    [Fact]
    public void PasswordEvidenceOnAnyGroupMemberCounts()
    {
        var group = new Receiver("stereo:pair", "Pair", "") { Members = [Physical(Bit40, "366.0", requiresPassword: true), AirPlay2()] };
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(group, Notice("session_rejected")));
    }

    [Theory]
    [InlineData("mfi_required", "synthetic")]
    [InlineData("protocol_error", "Device requires MFi authentication (not implementable without Apple hardware)")]
    [InlineData("authentication_failed", "MFi coprocessor rejected the request")]
    public void MfiLookingTextIsOnlyADisplayHeuristic(string code, string message)
    {
        var notice = Notice(code, message: message);
        // The authoritative kind stays the general authentication gate: the
        // desktop has no reliable MFi signal to act on (Q37).
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(AirPlay2(), notice));
        Assert.Equal(L.Get("This device requires MFi authentication, which AirFlash cannot perform without Apple hardware."), GateFailure.Message(AirPlay2(), notice));
    }

    [Fact]
    public void MfiLookingTextStillWinsOverClassicEvidenceOnlyForDisplay()
    {
        var notice = Notice("protocol_error", message: "Device requires MFi authentication");
        Assert.Equal(GateFailureKind.PasswordOrPairing, GateFailure.Classify(Classic(), notice));
        Assert.Equal(L.Get("This device requires MFi authentication, which AirFlash cannot perform without Apple hardware."), GateFailure.Message(Classic(), notice));
    }

    [Theory]
    [InlineData("network_error")]
    [InlineData("socket_error")]
    [InlineData("connection_reset")]
    [InlineData("read_timeout")]
    [InlineData("write_failed")]
    [InlineData("peer_closed")]
    [InlineData("feedback_timeout")]
    public void NetworkCodesKeepTheirExplanation(string code)
    {
        Assert.Equal(GateFailureKind.Network, GateFailure.Classify(AirPlay2(), Notice(code)));
        Assert.Equal(L.Get("Could not reach the receiver over the network. Check that it is powered on and on the same network as this PC."), GateFailure.Message(AirPlay2(), Notice(code)));
    }

    [Theory]
    [InlineData("protocol_error")]
    [InlineData("engine_error")]
    [InlineData("host_error")]
    [InlineData("preempted")]
    [InlineData("timeline_expired")]
    [InlineData("clock_lost")]
    public void CodesWithoutGateEvidenceKeepTheEngineDetail(string code)
    {
        Assert.Equal(GateFailureKind.None, GateFailure.Classify(AirPlay2(), Notice(code)));
        Assert.Null(GateFailure.Message(AirPlay2(), Notice(code)));
    }
}
