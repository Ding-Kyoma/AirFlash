namespace AirFlash.Core;

/// <summary>Resolves explicit playback identity and ranks eligible automatic candidates without side effects.</summary>
public static class ReceiverSelection
{
    /// <summary>Returns the confirmed owner, the unchanged selection when no owner is known, or null for ambiguous ownership.</summary>
    public static Receiver? ResolvePlayback(Receiver selected, IEnumerable<Receiver> catalog)
    {
        if (selected.IsManual || selected.IsGroup) return selected;
        var id = ReceiverIdentity.Normalize(selected.Id);
        if (id.Length == 0) return selected;
        var broadcast = ReceiverIdentity.IsBroadcast(id);
        var owners = catalog.Where(root => root.Online && !root.IsManual)
            .SelectMany(root => root.Peers.Where(member => member.Online && !member.IsManual).Select(member => (Root: root, Member: member)))
            .Where(owner => ReceiverIdentity.Normalize(owner.Member.Id) == id
                || broadcast && owner.Member.Aliases.Any(alias => ReceiverIdentity.IsBroadcast(alias) && ReceiverIdentity.Normalize(alias) == id))
            .ToArray();
        if (owners.Length == 0) return selected;
        // An alias shared by distinct physical receivers is not proof of group ownership.
        if (owners.Select(owner => ReceiverIdentity.Normalize(owner.Member.Id)).Distinct(StringComparer.Ordinal).Take(2).Count() > 1) return null;
        var groups = owners.Where(owner => owner.Root.IsGroup).Select(owner => owner.Root).DistinctBy(group => group.Id, StringComparer.Ordinal).Take(2).ToArray();
        return groups.Length switch { 0 => selected, 1 => groups[0], _ => null };
    }

    public static bool IsAutoConnectEligible(Receiver receiver, AppSettings settings, IReadOnlySet<string> attempted)
    {
        var options = settings.ReadOptions(receiver.Id);
        return receiver.Online && receiver.Complete && !options.Hidden && !attempted.Contains(receiver.Id)
            && (options.AutoConnect ?? settings.AutoConnectOnDiscover);
    }

    public static Receiver? SelectAutoConnect(IEnumerable<Receiver> catalog, AppSettings settings, IReadOnlySet<string> attempted)
        => catalog.Where(receiver => IsAutoConnectEligible(receiver, settings, attempted))
            .OrderByDescending(receiver => receiver.Id == settings.LastReceiverId)
            .ThenBy(receiver => receiver.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(receiver => receiver.Id, StringComparer.Ordinal).FirstOrDefault();
}
