namespace AirFlash.Core;

internal static class DiscoveryFailureAttribution
{
    internal static bool IsOtherReceiver(DiscoveryResolveContext source, Receiver active, IEnumerable<Receiver> catalog,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (!ReceiverIdentity.IsBroadcast(source.DeviceId) || active.IsManual) return false;
        var identity = ReceiverIdentity.Normalize(source.DeviceId);
        var canonical = ReceiverIdentity.Resolve(identity, aliases);
        var endpoint = ReceiverIdentity.Endpoint(source.Address, source.Port);

        // A stale or conflicting record is insufficient. Its identity and endpoint must
        // still point to the same unique physical receiver in the live discovery catalog.
        var candidates = catalog.Where(row => row.Online && !row.IsManual).SelectMany(row => row.Peers)
            .Where(peer => peer.Online && !peer.IsManual)
            .Select(peer => (Peer: peer, Keys: Keys(peer, aliases)))
            .Where(candidate => candidate.Keys.Contains(identity) || candidate.Keys.Contains(canonical) || candidate.Keys.Contains(endpoint))
            .Take(2).ToArray();
        if (candidates.Length != 1) return false;
        var (other, otherKeys) = candidates[0];
        if (!ReceiverIdentity.IsBroadcast(other.DeviceId) || !otherKeys.Contains(endpoint)
            || !(otherKeys.Contains(identity) || otherKeys.Contains(canonical))
            || source.StereoId.Length > 0 && other.StereoId.Length > 0 && !SameStereo(source.StereoId, other.StereoId)) return false;

        var peers = active.Peers.ToArray();
        if (peers.Length == 0 || SameStereo(source.StereoId, active.StereoId) || SameStereo(other.StereoId, active.StereoId)) return false;
        foreach (var peer in peers)
        {
            if (peer.IsManual || !ReceiverIdentity.IsBroadcast(peer.DeviceId) || SameStereo(source.StereoId, peer.StereoId)
                || SameStereo(other.StereoId, peer.StereoId)) return false;
            var activeKeys = Keys(peer, aliases);
            if (activeKeys.Overlaps(otherKeys) || activeKeys.Contains(identity) || activeKeys.Contains(canonical)
                || activeKeys.Select(EndpointHost).OfType<string>().Intersect(otherKeys.Select(EndpointHost).OfType<string>(), StringComparer.OrdinalIgnoreCase).Any()) return false;
        }
        return true;
    }

    private static HashSet<string> Keys(Receiver receiver, IReadOnlyDictionary<string, string> aliases)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { ReceiverIdentity.Endpoint(receiver.Address, receiver.Port) };
        foreach (var value in receiver.Aliases.Append(receiver.Id).Append(receiver.DeviceId))
        {
            if (ReceiverIdentity.TryEndpoint(value, out var address, out var port)) keys.Add(ReceiverIdentity.Endpoint(address, port));
            else if (value.Length > 0) keys.Add(ReceiverIdentity.Normalize(value));
            if (value.Length > 0) keys.Add(ReceiverIdentity.Resolve(value, aliases));
        }
        return keys;
    }

    private static string? EndpointHost(string value) => ReceiverIdentity.TryEndpoint(value, out var host, out _) ? host : null;
    private static bool SameStereo(string left, string right) => left.Length > 0 && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
