namespace AirFlash.Core;

public static class SoftwareStereo
{
    public static IReadOnlyList<Receiver> Resolve(IEnumerable<Receiver> catalog, AppSettings settings)
    {
        var roots = catalog.Where(r => !r.IsSoftwareGroup).ToArray();
        var physical = roots.SelectMany(r => r.Peers).ToArray();
        Receiver Member(string id, string channel, int offset)
        {
            id = ReceiverIdentity.Resolve(id, settings.ReceiverAliases);
            var found = FindMember(id, physical, roots) ?? Receiver.Offline(id);
            return found with { Channel = channel, SyncOffsetMs = offset };
        }
        return settings.SoftwareGroups.Select(g =>
        {
            var members = new[] { Member(g.LeftId, "left", g.LeftOffsetMs), Member(g.RightId, "right", g.RightOffsetMs) };
            // Q45: restoring a saved group goes through the same invariant as the
            // editor, but the invariant is three-valued. TryAdd accepts members
            // whose capability is Unknown (offline, no TXT, unparsable srcvers);
            // the contradiction is caught here, once both sides have evidence.
            // The saved group is never modified by resolution: it resolves to an
            // unusable group with no members, and the settings editor offers the
            // explicit split action.
            var membership = new GroupMembership();
            foreach (var member in members) membership.TryAdd(member, out _);
            var homogeneous = !GroupMembership.HasProvenConflict(members);
            var usable = homogeneous && members.All(m => m.Online && !m.IsGroup && m.StereoId.Length == 0) && members.Select(m => Receiver.PhysicalKey(m.DeviceId, m.Address)).Distinct().Count() == 2 && members.Select(m => m.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2;
            return new Receiver(g.Id, g.Name, "") { IsSoftwareGroup = true, Online = usable, Members = homogeneous ? membership.Members.ToArray() : [] };
        }).ToArray();
    }

    /// <summary>
    /// Resolves one saved member id against a discovery/root catalog. Used by
    /// <see cref="Resolve"/> and by the Q45 migration, which needs the same
    /// evidence the session would see. A missing member is an offline receiver
    /// with no TXT evidence, never a classic one.
    /// </summary>
    public static Receiver Member(string id, IEnumerable<Receiver> catalog, AppSettings settings)
    {
        var roots = catalog.Where(r => !r.IsSoftwareGroup).ToArray();
        var resolved = ReceiverIdentity.Resolve(id, settings.ReceiverAliases);
        return FindMember(resolved, roots.SelectMany(r => r.Peers).ToArray(), roots) ?? Receiver.Offline(resolved);
    }

    private static Receiver? FindMember(string id, Receiver[] physical, Receiver[] roots)
    {
        var found = physical.FirstOrDefault(r => r.Id == id || r.Aliases.Contains(id, StringComparer.Ordinal));
        if (found is not null && roots.Any(r => r.IsGroup && r.Members.Any(m => m.Id == found.Id)))
            found = found with { StereoId = found.StereoId.Length > 0 ? found.StereoId : "native" };
        return found;
    }

    /// <summary>
    /// The explicit repair action (QUESTIONS Q45). A saved pair that became
    /// heterogeneous because a device's TXT record changed is never split
    /// automatically — the user presses "Split into two independent receivers",
    /// which removes only the group record. Every per-device option (volume,
    /// latency, codec, sample rate, hidden/auto-connect, the device's saved
    /// name) stays in <see cref="AppSettings.Receivers"/> untouched, so both
    /// receivers remain exactly as the user configured them.
    /// </summary>
    public static bool Split(AppSettings settings, string groupId)
    {
        var group = settings.SoftwareGroups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return false;
        settings.SoftwareGroups.Remove(group);
        // The group's own options record is group-level, not a device setting.
        settings.Receivers.Remove(group.Id);
        return true;
    }

    public static IReadOnlyList<Receiver> Candidates(IEnumerable<Receiver> catalog) => catalog.Where(r => !r.IsGroup && r.StereoId.Length == 0).ToArray();
}
