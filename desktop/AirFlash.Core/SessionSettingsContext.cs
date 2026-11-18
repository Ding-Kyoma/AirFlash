namespace AirFlash.Core;

// Catalog identities describe persisted preferences; a live transport can still
// own an older identity while an unrelated identity-save finishes. This context
// is frozen by the caller under its settings gate and is never persisted.
public sealed class SessionSettingsContext
{
    private readonly Receiver[] _catalog;
    private readonly Dictionary<string, string> _migrations;
    private readonly HashSet<string> _clearedOptions;
    private readonly HashSet<string> _changedMemberships;

    public SessionSettingsContext(IEnumerable<Receiver> catalog, IReadOnlyDictionary<string, string> migrations,
        IEnumerable<string>? clearedOptions = null, IEnumerable<string>? changedMemberships = null)
    {
        _catalog = catalog.SelectMany(receiver => receiver.IsGroup ? receiver.Peers.Append(receiver) : [receiver])
            .Select(receiver => receiver with { Members = receiver.Members.ToArray() }).ToArray();
        _migrations = migrations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _clearedOptions = (clearedOptions ?? []).ToHashSet(StringComparer.Ordinal);
        _changedMemberships = (changedMemberships ?? []).ToHashSet(StringComparer.Ordinal);
    }

    internal AppSettings Project(AppSettings incoming, AppSettings owned, Receiver? receiver)
    {
        var effective = incoming.Clone();
        if (receiver is null) return effective;
        foreach (var active in receiver.Peers.Append(receiver).DistinctBy(item => item.Id))
        {
            var target = _migrations.GetValueOrDefault(active.Id, ReceiverIdentity.Resolve(active.Id, incoming.ReceiverAliases));
            var bound = target != active.Id && !active.IsManual && HasUniqueTransport(active, target);
            // A present value (including defaults) or an explicit removal is a
            // deliberate edit. Mere absence after migration is not a reset.
            if (incoming.Receivers.ContainsKey(active.Id) || _clearedOptions.Contains(active.Id)) continue;
            if (bound && _clearedOptions.Contains(target)) continue;
            if (bound && incoming.Receivers.TryGetValue(target, out var current))
                effective.Options(active.Id).CopyFrom(current);
            else if (owned.Receivers.TryGetValue(active.Id, out var previous))
                effective.Options(active.Id).CopyFrom(previous);
        }
        if (receiver.IsSoftwareGroup && !_changedMemberships.Contains(receiver.Id)
            && effective.SoftwareGroups.FirstOrDefault(group => group.Id == receiver.Id) is { } group)
        {
            // An ordinary save must not replace owned members with the latest
            // catalog. This also works after an adapter restart loses all alias
            // evidence. Explicit membership edits use the separate admission path.
            if (receiver.IsMultiRoom && group.IsMultiRoom)
            {
                var manualIds = incoming.ManualReceivers.Select(member => member.Id)
                    .Concat(receiver.Members.Where(member => member.IsManual).Select(member => member.Id))
                    .ToHashSet(StringComparer.Ordinal);
                (bool Manual, string Id) MemberKey(string id) => manualIds.Contains(id)
                    ? (true, id)
                    : (false, ReceiverIdentity.Resolve(_migrations.GetValueOrDefault(id, id), incoming.ReceiverAliases));
                var incomingMembers = group.Members.ToLookup(member => MemberKey(member.ReceiverId));
                var ownedMembers = receiver.Members.ToLookup(member => MemberKey(member.Id));
                group.Members.Clear();
                foreach (var member in receiver.Members)
                {
                    var key = MemberKey(member.Id);
                    var saved = incomingMembers[key].Take(2).ToArray();
                    var offset = saved.Length == 1 && !ownedMembers[key].Skip(1).Any() ? saved[0].OffsetMs : member.SyncOffsetMs;
                    group.Members.Add(new() { ReceiverId = member.Id, OffsetMs = offset });
                }
            }
            else if (!receiver.IsMultiRoom && !group.IsMultiRoom)
            {
                foreach (var member in receiver.Members)
                {
                    if (member.Channel == "left") group.LeftId = member.Id;
                    else if (member.Channel == "right") group.RightId = member.Id;
                }
            }
            foreach (var member in receiver.Members)
            {
                effective.ReceiverAliases.Remove(member.Id);
            }
        }
        return effective;
    }

    private bool HasUniqueTransport(Receiver active, string target)
    {
        var identities = _catalog.Where(candidate => !candidate.IsManual && SameTransport(active, candidate))
            .Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).ToArray();
        return identities.Length == 1 && identities[0] == target;
    }

    private static bool SameTransport(Receiver left, Receiver right)
    {
        if (left.IsGroup != right.IsGroup || left.IsSoftwareGroup != right.IsSoftwareGroup) return false;
        var expected = left.Peers.OrderBy(member => member.Address, StringComparer.OrdinalIgnoreCase).ThenBy(member => member.Port).ToArray();
        var actual = right.Peers.OrderBy(member => member.Address, StringComparer.OrdinalIgnoreCase).ThenBy(member => member.Port).ToArray();
        return expected.Length == actual.Length && expected.Zip(actual).All(pair => pair.First.Port == pair.Second.Port
            && string.Equals(pair.First.Address, pair.Second.Address, StringComparison.OrdinalIgnoreCase));
    }
}
