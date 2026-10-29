namespace AirFlash.Core;

/// <summary>
/// Three-valued AirPlay routing evidence (QUESTIONS Q45). The strict routing
/// boolean <see cref="Receiver.SupportsAirPlay2"/> folds "no evidence" into
/// "classic"; mixing inside a group must not do that.
/// </summary>
public enum Ap2Evidence
{
    /// <summary>No TXT, the device is offline, or <c>srcvers</c> is missing/unparsable: no evidence either way.</summary>
    Unknown,
    /// <summary>Positive evidence that the device takes the AirPlay 2 path.</summary>
    Yes,
    /// <summary>Positive evidence of the classic path: bit 40 clear, or a too-old <c>srcvers</c>.</summary>
    No
}

/// <summary>
/// Maps a receiver's display label to the routing evidence used by the group
/// invariant. <see cref="AirPlayCapability.ClaimsAirPlay2Outdated"/> is routing
/// false (Q36), so it is a proven classic member, not an unknown one; only a
/// missing TXT record or an unparsable <c>srcvers</c> is unknown.
/// </summary>
public static class AirPlayEvidence
{
    public static Ap2Evidence Of(Receiver receiver) => receiver.Support switch
    {
        AirPlayCapability.AirPlay2 => Ap2Evidence.Yes,
        AirPlayCapability.NotAirPlay2 or AirPlayCapability.ClaimsAirPlay2Outdated => Ap2Evidence.No,
        _ => Ap2Evidence.Unknown
    };

    /// <summary>Only "one side proven AirPlay 2, the other proven classic" is a contradiction.</summary>
    public static bool Contradict(Ap2Evidence left, Ap2Evidence right)
        => left is Ap2Evidence.Yes && right is Ap2Evidence.No
        || left is Ap2Evidence.No && right is Ap2Evidence.Yes;
}

/// <summary>
/// A group's member list (QUESTIONS Q41/Q45), in the shape WinAirCast's
/// <c>AddMember</c> (<c>:8316</c>) uses: <see cref="TryAdd"/> is the only way a
/// receiver enters. It refuses only a <em>proven</em> contradiction — one side
/// proven AirPlay 2 and the other proven classic. A receiver whose capability is
/// <see cref="Ap2Evidence.Unknown"/> (offline, no TXT, no <c>srcvers</c>) is
/// accepted: absence of evidence is not evidence of absence (Q45). The real
/// verdict is deferred to <see cref="SoftwareStereo.Resolve"/>, which runs when
/// every member's TXT record is available.
///
/// Stereo pairs: this deliberately does NOT copy WinAirCast's
/// <c>!IsCustomStereoPair</c> exemption. AirFlash's user-built software stereo
/// group is two independent receivers joined by this app (channel plus
/// per-member offset) and sent to the engine as <c>software_stereo</c> peers;
/// there is no <c>SetStereoPair</c> hand-off that makes the pair one logical
/// unit, and the engine's <c>select_timing</c> refuses a mixed pair exactly like
/// any other group. Exempting it here would construct a group the backend
/// rejects. Factory HomePod pairs arrive as one mDNS <c>tsid</c> discovery unit
/// and are never added through this list.
/// </summary>
public sealed class GroupMembership
{
    private readonly List<Receiver> _members = [];
    public IReadOnlyList<Receiver> Members => _members;
    public int Count => _members.Count;

    /// <summary>
    /// Adds <paramref name="receiver"/> unless it is a proven contradiction of
    /// any existing member. The first receiver is always accepted (WinAirCast's
    /// <c>Members.Count &lt;= 0 ||</c> short-circuit), which keeps a
    /// single-receiver choice untouched. On refusal the list is unchanged and
    /// <paramref name="error"/> carries the user-facing two-level message.
    /// </summary>
    public bool TryAdd(Receiver receiver, out string? error)
    {
        var incoming = AirPlayEvidence.Of(receiver);
        if (_members.Any(member => AirPlayEvidence.Contradict(AirPlayEvidence.Of(member), incoming)))
        {
            error = MixedGroupMessage.For(_members.Append(receiver));
            return false;
        }
        error = null;
        _members.Add(receiver);
        return true;
    }

    /// <summary>Convenience overload for paths that only care about success.</summary>
    public bool TryAdd(Receiver receiver) => TryAdd(receiver, out _);

    /// <summary>
    /// True when a member set contains both proven AirPlay 2 and proven classic
    /// evidence. This is the check <see cref="SoftwareStereo.Resolve"/> uses after
    /// accepting unknowns; the saved group is left untouched and only marked
    /// unusable.
    /// </summary>
    public static bool HasProvenConflict(IEnumerable<Receiver> members)
    {
        var evidence = members.Select(AirPlayEvidence.Of).ToArray();
        return evidence.Contains(Ap2Evidence.Yes) && evidence.Contains(Ap2Evidence.No);
    }
}

/// <summary>
/// The user-facing mixed-timing-group message. The action sentence ("connect
/// them separately") is always first and always present; a reason naming the
/// offending device is only appended when its display label is
/// <see cref="AirPlayCapability.ClaimsAirPlay2Outdated"/>. The reason never
/// replaces the action: a user told only "firmware too old" would update the
/// firmware and still not know the two devices must be connected separately.
/// </summary>
public static class MixedGroupMessage
{
    public const string ActionKey = "AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together. Please connect them separately.";
    public const string OutdatedReasonKey = "“{0}” claims AirPlay 2, but its firmware is older than 354.54.6, so it is treated as a classic device.";
    /// <summary>
    /// Q45's add-time notice when an accepted member has no capability evidence.
    /// This is a warning, not an error: the pair is representable until a TXT
    /// record proves it mixed.
    /// </summary>
    public const string UnknownCapabilityKey = "A member's AirPlay 2 capability is not known right now. The group can be saved, but the connection will be refused if the two members turn out to be incompatible.";

    public static string Action => L.Get(ActionKey);

    public static string For(IEnumerable<Receiver> members)
    {
        var outdated = members.FirstOrDefault(member => member.Support == AirPlayCapability.ClaimsAirPlay2Outdated);
        return outdated is null ? Action : Action + " " + L.Format(OutdatedReasonKey, outdated.Name);
    }
}
