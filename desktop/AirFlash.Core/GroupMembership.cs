namespace AirFlash.Core;

/// <summary>
/// A group's member list (QUESTIONS Q41), in the shape WinAirCast's
/// <c>AddMember</c> (<c>:8316</c>) uses: <see cref="TryAdd"/> is the only way a
/// receiver enters, and it refuses a receiver whose AirPlay routing boolean
/// differs from the existing members'. A mixed timing group is therefore not
/// "checked before connecting" — it cannot be constructed in the first place.
///
/// The one predicate is <see cref="Receiver.SupportsAirPlay2"/> (TXT bit 40 +
/// <c>srcvers</c>, i.e. <see cref="AirPlayRouting.SupportsAirPlay2"/>), not the
/// four-state display label: homogeneity asks which protocol path the group
/// takes, while <see cref="AirPlayCapability"/> exists to explain what the user
/// sees (Q36/Q41). A receiver without TXT evidence routes classic under the same
/// strict predicate, so it is treated as classic here too.
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
    /// Adds <paramref name="receiver"/> when it routes like the existing
    /// members. The first receiver is always accepted (WinAirCast's
    /// <c>Members.Count &lt;= 0 ||</c> short-circuit), which keeps a
    /// single-receiver choice untouched. On refusal the list is unchanged and
    /// <paramref name="error"/> carries the user-facing two-level message.
    /// </summary>
    public bool TryAdd(Receiver receiver, out string? error)
    {
        if (_members.Count > 0 && _members[0].SupportsAirPlay2 != receiver.SupportsAirPlay2)
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

    public static string Action => L.Get(ActionKey);

    public static string For(IEnumerable<Receiver> members)
    {
        var outdated = members.FirstOrDefault(member => member.Support == AirPlayCapability.ClaimsAirPlay2Outdated);
        return outdated is null ? Action : Action + " " + L.Format(OutdatedReasonKey, outdated.Name);
    }
}
