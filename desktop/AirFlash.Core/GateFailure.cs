namespace AirFlash.Core;

/// <summary>Actionable reason a session could not start.</summary>
public enum GateFailureKind
{
    /// <summary>No actionable evidence; keep the engine's own fault detail.</summary>
    None,
    /// <summary>TXT proves the receiver is classic-only, so AirFlash cannot stream to it today.</summary>
    ClassicOnly,
    /// <summary>
    /// Password/pairing failure, or any authentication failure the desktop cannot
    /// attribute further. MFi-looking text lands here too (QUESTIONS Q37): it is a
    /// display heuristic, not an authoritative MFi classification.
    /// </summary>
    PasswordOrPairing,
    /// <summary>The receiver could not be reached or stopped answering on the network.</summary>
    Network,
    /// <summary>
    /// The engine refused a group that mixes AirPlay 2 and classic devices before
    /// any SETUP (QUESTIONS Q39). Like WAC, AirFlash does not downgrade such a
    /// group; the user has to connect the two device types separately.
    /// </summary>
    MixedTimingGroup
}

/// <summary>
/// Turns a session failure into one of the gate reasons the desktop can explain
/// from evidence it actually has. The engine's distinct codes are mapped first;
/// TXT evidence is used only for the classic-only gate, and only on the setup
/// channel so a mid-session network fault is never relabelled as a capability
/// gate.
/// </summary>
public static class GateFailure
{
    private static readonly HashSet<string> NetworkCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "network_error", "socket_error", "connection_reset", "connection_refused",
        "read_timeout", "write_failed", "peer_closed", "feedback_timeout"
    };
    private static readonly HashSet<string> UnreachableCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "network_error", "socket_error", "connection_reset", "connection_refused"
    };

    public static GateFailureKind Classify(Receiver? receiver, EngineNotice? notice)
    {
        if (notice is null || notice.Code is "preempted" or "timeline_expired" or "clock_lost")
            return GateFailureKind.None;
        // The engine refuses a mixed group before any SETUP, so this code is
        // authoritative. It must win over the classic-member evidence below:
        // those members are not the reason the session failed, and telling the
        // user to replace the classic device would be the wrong advice.
        if (notice.Code.Equals("mixed_timing_group", StringComparison.OrdinalIgnoreCase))
            return GateFailureKind.MixedTimingGroup;
        // MFi-looking text is folded into the general authentication gate: it may
        // only change the wording (see Message), never the authoritative kind.
        if (LooksLikeMfi(notice) || IsPassword(notice, receiver)) return GateFailureKind.PasswordOrPairing;
        // A classic receiver often accepts TCP but cannot answer AirPlay 2 /info
        // at all, so a setup read timeout still points at the capability gate.
        // An unreachable socket (or DNS) failure keeps its network explanation.
        if (HasClassicEvidence(receiver) && notice.Channel.Equals("setup", StringComparison.OrdinalIgnoreCase)
            && !UnreachableCodes.Contains(notice.Code))
            return GateFailureKind.ClassicOnly;
        if (NetworkCodes.Contains(notice.Code)) return GateFailureKind.Network;
        return GateFailureKind.None;
    }

    public static string? Message(Receiver? receiver, EngineNotice? notice)
    {
        var kind = Classify(receiver, notice);
        // The only MFi-specific behaviour is friendlier wording for a failure the
        // desktop cannot actually distinguish from a generic authentication error.
        if (kind == GateFailureKind.PasswordOrPairing && LooksLikeMfi(notice))
            return L.Get("This device requires MFi authentication, which AirFlash cannot perform without Apple hardware.");
        return kind switch
        {
            GateFailureKind.ClassicOnly => L.Get("This device only supports classic AirPlay. AirFlash cannot stream to it yet."),
            GateFailureKind.PasswordOrPairing => L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."),
            GateFailureKind.Network => L.Get("Could not reach the receiver over the network. Check that it is powered on and on the same network as this PC."),
            GateFailureKind.MixedTimingGroup => L.Get("AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together. Please connect them separately."),
            _ => null
        };
    }

    /// <summary>
    /// WAC's group rule for the classic-only explanation: any member with positive
    /// evidence that it is not AirPlay 2 makes the whole group classic. An
    /// outdated AirPlay 2 claim (<see cref="AirPlayCapability.ClaimsAirPlay2Outdated"/>)
    /// is not evidence of absence and is deliberately excluded (Q36); routing still
    /// sends it down the classic path via <see cref="Receiver.SupportsAirPlay2"/>.
    /// </summary>
    public static bool HasClassicEvidence(Receiver? receiver)
        => receiver is not null && receiver.Peers.Any(peer => peer.Support == AirPlayCapability.NotAirPlay2);

    /// <summary>
    /// Best-effort display heuristic only (QUESTIONS Q37). WAC defines an MFi
    /// error variant but never detects it ("not implementable without Apple
    /// hardware"), and neither the engine nor the TXT record exposes an MFi
    /// signal. Never use this to pick a gate kind.
    /// </summary>
    private static bool LooksLikeMfi(EngineNotice? notice)
        => notice is not null
            && (notice.Code.Equals("mfi_required", StringComparison.OrdinalIgnoreCase)
                || notice.Message.Contains("MFi", StringComparison.OrdinalIgnoreCase));

    private static bool IsPassword(EngineNotice notice, Receiver? receiver)
        => notice.Code.Equals("authentication_failed", StringComparison.OrdinalIgnoreCase)
            || notice.Code.Equals("session_rejected", StringComparison.OrdinalIgnoreCase)
                && receiver is not null && receiver.Peers.Any(peer => peer.RequiresPassword);
}
