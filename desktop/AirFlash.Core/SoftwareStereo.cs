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
            var found = physical.FirstOrDefault(r => r.Id == id || r.Aliases.Contains(id, StringComparer.Ordinal)) ?? Receiver.Offline(id);
            if (roots.Any(r => r.IsGroup && r.Members.Any(m => m.Id == found.Id))) found = found with { StereoId = found.StereoId.Length > 0 ? found.StereoId : "native" };
            return found with { Channel = channel, SyncOffsetMs = offset };
        }
        return settings.SoftwareGroups.Select(g =>
        {
            var members = new[] { Member(g.LeftId, "left", g.LeftOffsetMs), Member(g.RightId, "right", g.RightOffsetMs) };
            var usable = members.All(m => m.Online && !m.IsGroup && m.StereoId.Length == 0) && members.Select(m => Receiver.PhysicalKey(m.DeviceId, m.Address)).Distinct().Count() == 2 && members.Select(m => m.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2;
            return new Receiver(g.Id, g.Name, "") { IsSoftwareGroup = true, Online = usable, Members = members };
        }).ToArray();
    }
    public static IReadOnlyList<Receiver> Candidates(IEnumerable<Receiver> catalog) => catalog.Where(r => !r.IsGroup && r.StereoId.Length == 0).ToArray();
}
