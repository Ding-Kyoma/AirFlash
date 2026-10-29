namespace AirFlash.Core;

/// <summary>Actionable reason a session could not start.</summary>
public enum GateFailureKind
{
    /// <summary>No actionable evidence; keep the engine's own fault detail.</summary>
    None,
    /// <summary>TXT proves the receiver is classic-only, so AirFlash cannot stream to it today.</summary>
    ClassicOnly,
    /// <summary>The receiver wants a password or pairing AirFlash could not complete.</summary>
    PasswordOrPairing,
    /// <summary>The receiver asks for an MFi authentication coprocessor.</summary>
    MfiAuthentication,
    /// <summary>The receiver could not be reached or stopped answering on the network.</summary>
    Network
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
        if (IsMfi(notice)) return GateFailureKind.MfiAuthentication;
        if (IsPassword(notice, receiver)) return GateFailureKind.PasswordOrPairing;
        // A classic receiver often accepts TCP but cannot answer AirPlay 2 /info
        // at all, so a setup read timeout still points at the capability gate.
        // An unreachable socket (or DNS) failure keeps its network explanation.
        if (HasClassicEvidence(receiver) && notice.Channel.Equals("setup", StringComparison.OrdinalIgnoreCase)
            && !UnreachableCodes.Contains(notice.Code))
            return GateFailureKind.ClassicOnly;
        if (NetworkCodes.Contains(notice.Code)) return GateFailureKind.Network;
        return GateFailureKind.None;
    }

    public static string? Message(Receiver? receiver, EngineNotice? notice) => Classify(receiver, notice) switch
    {
        GateFailureKind.ClassicOnly => L.Get("This device only supports classic AirPlay. AirFlash cannot stream to it yet."),
        GateFailureKind.PasswordOrPairing => L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."),
        GateFailureKind.MfiAuthentication => L.Get("This device requires MFi authentication, which AirFlash cannot perform without Apple hardware."),
        GateFailureKind.Network => L.Get("Could not reach the receiver over the network. Check that it is powered on and on the same network as this PC."),
        _ => null
    };

    /// <summary>WAC's group rule: any classic member makes the whole group classic.</summary>
    public static bool HasClassicEvidence(Receiver? receiver)
        => receiver is not null && receiver.Peers.Any(peer => peer.Support == AirPlaySupport.ClassicOnly);

    private static bool IsMfi(EngineNotice notice)
        => notice.Code.Equals("mfi_required", StringComparison.OrdinalIgnoreCase)
            || notice.Message.Contains("MFi", StringComparison.OrdinalIgnoreCase);

    private static bool IsPassword(EngineNotice notice, Receiver? receiver)
        => notice.Code.Equals("authentication_failed", StringComparison.OrdinalIgnoreCase)
            || notice.Code.Equals("session_rejected", StringComparison.OrdinalIgnoreCase)
                && receiver is not null && receiver.Peers.Any(peer => peer.RequiresPassword);
}
