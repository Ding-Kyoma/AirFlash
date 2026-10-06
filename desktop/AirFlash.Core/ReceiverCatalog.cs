namespace AirFlash.Core;

public sealed record ReceiverReconciliation(IReadOnlyList<Receiver> Receivers, AppSettings Settings, IReadOnlyDictionary<string, string> Migrations, bool NeedsBackup);

/// <summary>Reconciles discovery aliases independently of UI and credentials. Endpoint aliases never persist.</summary>
public static class ReceiverCatalog
{
    public static ReceiverReconciliation Reconcile(IReadOnlyList<Receiver> receivers, AppSettings original, string? activeId = null)
    {
        var settings = original.Clone();
        var manual = settings.ManualReceivers.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var physical = receivers.Where(r => !r.IsManual).SelectMany(r => r.Peers).ToArray();
        var endpoints = physical.SelectMany(r => r.Aliases.Append(ReceiverIdentity.Endpoint(r.Address, r.Port))
            .Where(id => ReceiverIdentity.TryEndpoint(id, out _, out _)).Select(id => (Endpoint: NormalizeEndpoint(id), Receiver: r.Id)))
            .Distinct().GroupBy(p => p.Endpoint).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        bool UniqueEndpoint(string id) => endpoints.GetValueOrDefault(NormalizeEndpoint(id)) == 1;
        var liveIdentities = physical.SelectMany(r => r.Aliases.Append(r.Id).Where(ReceiverIdentity.IsBroadcast).Select(id => (Alias: ReceiverIdentity.Normalize(id), Receiver: r.Id)))
            .GroupBy(p => p.Alias).ToDictionary(g => g.Key, g => g.Select(p => p.Receiver).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var mappedOwners = physical.SelectMany(r => r.Aliases.Append(r.Id).Where(ReceiverIdentity.IsBroadcast).Select(id => ReceiverIdentity.Resolve(id, original.ReceiverAliases)).Distinct(StringComparer.Ordinal))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var migrations = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = receivers.Select(root => root.IsManual ? root : root.IsGroup
            ? root with { Members = root.Members.Select(ReconcileMember).OrderByDescending(r => r.IsLeader).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray() }
            : ReconcileMember(root)).ToArray();
        // Remove intermediate targets and preserve a flat durable alias map.
        foreach (var key in settings.ReceiverAliases.Keys.ToArray())
            settings.ReceiverAliases[key] = ReceiverIdentity.Resolve(settings.ReceiverAliases[key], settings.ReceiverAliases);
        var backup = original.Receivers.Keys.Any(k => migrations.TryGetValue(k, out var target) && target != k)
            || original.LastReceiverId is { } last && migrations.TryGetValue(last, out var next) && next != last;
        ApplyMigrations(settings, migrations, activeId);
        return new(result, settings, migrations, backup);

        Receiver ReconcileMember(Receiver receiver)
        {
            var endpoint = ReceiverIdentity.Endpoint(receiver.Address, receiver.Port);
            if (manual.Contains(receiver.Id)) receiver = receiver with { Id = "auto-endpoint:" + endpoint };
            var advertisedEndpoints = receiver.Aliases.Append(endpoint).Where(id => ReceiverIdentity.TryEndpoint(id, out _, out _)).Select(NormalizeEndpoint).ToHashSet(StringComparer.Ordinal);
            var observed = receiver.Aliases.Append(receiver.Id).Append(receiver.DeviceId)
                .Where(id => id.Length > 0 && !manual.Contains(id) && (!ReceiverIdentity.TryEndpoint(id, out _, out _) || UniqueEndpoint(id))).Distinct(StringComparer.Ordinal).ToArray();
            var identities = observed.Where(ReceiverIdentity.IsBroadcast).Select(ReceiverIdentity.Normalize).Distinct(StringComparer.Ordinal).ToArray();
            bool Allowed(string id) => !liveIdentities.TryGetValue(ReceiverIdentity.Normalize(id), out var owners) || owners.All(owner => owner == receiver.Id);
            var mapped = identities.Where(settings.ReceiverAliases.ContainsKey).Select(id => ReceiverIdentity.Resolve(id, settings.ReceiverAliases))
                .Where(id => !manual.Contains(id) && Allowed(id) && mappedOwners.GetValueOrDefault(id) <= 1).Distinct(StringComparer.Ordinal).ToArray();
            var candidates = identities.Concat(mapped).ToHashSet(StringComparer.Ordinal);
            var configured = settings.Receivers.Keys.Where(k => !manual.Contains(k) && Allowed(k) && (candidates.Contains(ReceiverIdentity.Normalize(k)) || candidates.Contains(ReceiverIdentity.Resolve(k, settings.ReceiverAliases)))).ToArray();
            string? Preferred(IEnumerable<string> values)
                => values.OrderBy(id => ReceiverIdentity.Normalize(id) == ReceiverIdentity.Normalize(activeId ?? "") ? 0
                    : ReceiverIdentity.Normalize(id) == ReceiverIdentity.Normalize(original.LastReceiverId ?? "") ? 1 : 2)
                    .ThenBy(ReceiverIdentity.Normalize, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            var canonical = Preferred(mapped)
                ?? (activeId is not null && candidates.Contains(ReceiverIdentity.Normalize(activeId)) ? ReceiverIdentity.Normalize(activeId) : null)
                ?? (original.LastReceiverId is { } previous && candidates.Contains(ReceiverIdentity.Normalize(previous)) ? ReceiverIdentity.Normalize(previous) : null)
                ?? Preferred(configured.Select(ReceiverIdentity.Normalize))
                ?? (identities.Contains(ReceiverIdentity.Normalize(receiver.Id)) ? ReceiverIdentity.Normalize(receiver.Id) : Preferred(identities))
                ?? receiver.Id;
            var aliases = observed.Concat(configured).Append(canonical).ToHashSet(StringComparer.Ordinal);
            foreach (var key in settings.ReceiverAliases.Keys.ToArray())
                if (Allowed(key) && candidates.Contains(ReceiverIdentity.Resolve(key, settings.ReceiverAliases)))
                {
                    aliases.Add(key); settings.ReceiverAliases[key] = canonical;
                }
            foreach (var id in identities) settings.ReceiverAliases[id] = canonical;
            foreach (var key in settings.Receivers.Keys.Append(original.LastReceiverId ?? "").Append(activeId ?? ""))
            {
                if (manual.Contains(key) || key.Length == 0 || !Allowed(key)) continue;
                if (candidates.Contains(ReceiverIdentity.Resolve(key, original.ReceiverAliases))) aliases.Add(key);
                if (ReceiverIdentity.TryEndpoint(key, out _, out _) && UniqueEndpoint(key) && advertisedEndpoints.Contains(NormalizeEndpoint(key))) aliases.Add(key);
            }
            foreach (var alias in aliases)
            {
                if (manual.Contains(alias)) continue;
                if (ReceiverIdentity.TryEndpoint(alias, out _, out _) && !UniqueEndpoint(alias)) continue;
                if (!migrations.TryGetValue(alias, out var existing) || existing == canonical) migrations[alias] = canonical;
            }
            aliases.RemoveWhere(id => ReceiverIdentity.TryEndpoint(id, out _, out _) && !UniqueEndpoint(id));
            return receiver with { Id = canonical, Aliases = aliases.Order(StringComparer.Ordinal).ToArray() };
        }
    }

    private static string NormalizeEndpoint(string id) => ReceiverIdentity.TryEndpoint(id, out var host, out var port) ? ReceiverIdentity.Endpoint(host, port) : id;

    public static void ApplyMigrations(AppSettings settings, IReadOnlyDictionary<string, string> migrations, string? activeId = null)
    {
        var manual = settings.ManualReceivers.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in migrations.Where(p => !manual.Contains(p.Key) && !manual.Contains(p.Value)).GroupBy(p => p.Value))
        {
            var keys = group.Select(p => p.Key).Append(group.Key).Distinct(StringComparer.Ordinal).Where(settings.Receivers.ContainsKey)
                .OrderBy(id => ReceiverIdentity.Normalize(id) == ReceiverIdentity.Normalize(activeId ?? "") ? 0
                    : ReceiverIdentity.Normalize(id) == ReceiverIdentity.Normalize(settings.LastReceiverId ?? "") ? 1 : 2)
                .ThenBy(ReceiverIdentity.Normalize, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            if (keys.Length > 0)
            {
                var options = keys.Select(id => settings.Receivers[id]).ToArray();
                var merged = new ReceiverOptions
                {
                    Hidden = options.Any(o => o.Hidden), AutoConnect = options.Any(o => o.AutoConnect == false) ? false : options.Select(o => o.AutoConnect).FirstOrDefault(v => v.HasValue),
                    Volume = options.Select(o => o.Volume).FirstOrDefault(v => v.HasValue), LatencyMode = options.Select(o => o.LatencyMode).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                    CustomBufferMs = options.Select(o => o.CustomBufferMs).FirstOrDefault(v => v.HasValue), StandbySeconds = options.Select(o => o.StandbySeconds).FirstOrDefault(v => v.HasValue),
                    TransportMode = options.Select(o => o.TransportMode).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)), TimingMode = options.Select(o => o.TimingMode).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                };
                settings.Options(group.Key).CopyFrom(merged);
                foreach (var key in keys.Where(k => k != group.Key)) settings.Receivers.Remove(key);
            }
        }
        if (settings.LastReceiverId is { } last && !manual.Contains(last) && migrations.TryGetValue(last, out var target)) settings.LastReceiverId = target;
    }

    public static bool IsCoveredMember(string id, IEnumerable<Receiver> receivers)
        => receivers.Where(r => r.IsGroup && r.Online).SelectMany(r => r.Members).Any(r => r.Online && (r.Id == id || r.Aliases.Contains(id, StringComparer.Ordinal)));
}
