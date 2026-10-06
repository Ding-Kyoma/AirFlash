using System.Text.Json.Serialization;
namespace AirFlash.Core;

public sealed record Receiver(string Id, string Name, string Address, int Port = 7000)
{
    public string DeviceId { get; init; } = "";
    public string Model { get; init; } = "";
    public string StereoId { get; init; } = "";
    public string GroupName { get; init; } = "";
    public bool IsLeader { get; init; }
    public bool IsManual { get; init; }
    public bool Online { get; init; } = true;
    public Receiver[] Members { get; init; } = [];
    public byte[] Codecs { get; init; } = [];
    public ServiceRecord[] Services { get; init; } = [];
    private static readonly HashSet<string> NegotiationFields = new(["features", "ft", "flags", "sf", "cn", "pw", "acl", "act", "sr", "model", "am", "deviceid"], StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public string CapabilitiesKey => System.Text.Json.JsonSerializer.Serialize(Services.Select(s => new { s.ServiceType, s.Address, s.Port, Txt = s.Txt.Where(p => NegotiationFields.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() }));
    public string[] Aliases { get; init; } = [];
    public bool IsGroup => Members.Length > 0 || Id.StartsWith("stereo:", StringComparison.Ordinal);
    public bool Complete => !IsGroup || Members.Length == 2;
    public string Detail => IsGroup ? L.Format("HomePod stereo · {0}/2", Members.Length) : Model.Length > 0 ? Model : L.Get("AirPlay receiver");
    [JsonIgnore] public IEnumerable<Receiver> Peers => IsGroup ? Members : [this];
    public string TransportKey => string.Join(";", Peers.OrderBy(p => p.Address, StringComparer.Ordinal).ThenBy(p => p.Port).Select(p => $"{p.Address}:{p.Port}:{(IsGroup && p.IsLeader)}:{p.CapabilitiesKey}"));
    public static string PhysicalKey(string identity, string address) => string.IsNullOrWhiteSpace(identity) ? address : ReceiverIdentity.Normalize(identity);
    public static Receiver Offline(string id) => ReceiverIdentity.TryEndpoint(id, out var address, out var port)
        ? new(id, L.Get("Offline receiver"), address, port) { Online = false }
        : new(id, L.Get("Offline receiver"), "") { Online = false };
}
public sealed record ServiceRecord(string Instance, string ServiceType, string Address, int Port, IReadOnlyDictionary<string, string> Txt);
public static class ReceiverAggregator
{
    public static IReadOnlyList<Receiver> Build(IEnumerable<ServiceRecord> services, Action<string>? diagnostic = null)
    {
        var groups = new List<List<ServiceRecord>>();
        foreach (var service in OrderServices(services))
        {
            var identity = ReceiverIdentity.Normalize(Identity(service));
            var group = identity.Length == 0 ? null : groups.FirstOrDefault(g => g.Any(s => ReceiverIdentity.Normalize(Identity(s)) == identity));
            var related = group?.Append(service).ToArray() ?? [service];
            var candidates = groups.Where(g => g != group && g.Any(s => Endpoint(s) == Endpoint(service))).ToArray();
            var compatible = candidates.Where(g => g.All(s => related.All(other => Compatible(s, other)))).ToArray();
            if (candidates.Any(g => !g.All(s => related.All(other => Compatible(s, other)))) || compatible.Length > 1)
                diagnostic?.Invoke("Discovery identity conflict: " + Describe(service) + "; fields=" + string.Join(",", candidates.SelectMany(g => g.SelectMany(s => related.SelectMany(other => Conflicts(s, other)))).Distinct(StringComparer.Ordinal)) + "; ambiguous_endpoint=" + (compatible.Length > 1));
            if (compatible.Length == 1)
            {
                if (group is null) group = compatible[0];
                else { group.AddRange(compatible[0]); groups.Remove(compatible[0]); }
            }
            if (group is null) groups.Add([service]);
            else group.Add(service);
        }
        var physical = groups.Select(records =>
        {
            var group = OrderServices(records).ToArray();
            var service = group[0];
            var name = service.Instance.Split("._", StringSplitOptions.None)[0];
            if (name.Contains('@')) name = name[(name.IndexOf('@') + 1)..];
            var identity = group.Select(Identity).FirstOrDefault(id => id.Length > 0) ?? "";
            var codecText = Field(group.Where(s => s.ServiceType.StartsWith("_raop", StringComparison.Ordinal)), "cn");
            var aliases = group.SelectMany(s => new[] { Identity(s), ReceiverIdentity.Normalize(Identity(s)), Endpoint(s) }).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (group.Length > 1) diagnostic?.Invoke("Discovery merged: " + string.Join(" | ", group.Select(Describe))
                + "; reason=" + (group.Select(s => ReceiverIdentity.Normalize(Identity(s))).Distinct(StringComparer.Ordinal).Count() == 1 ? "normalized_identity" : "compatible_endpoint"));
            return new Receiver(identity.Length > 0 ? ReceiverIdentity.Normalize(identity) : Endpoint(service), name, service.Address, service.Port)
            {
                DeviceId = identity,
                Model = group.Select(Model).FirstOrDefault(v => v.Length > 0) ?? "",
                StereoId = Field(group, "tsid"),
                GroupName = Field(group, "gpn"),
                IsLeader = Field(group, "igl") == "1",
                Aliases = aliases,
                Services = group,
                Codecs = codecText.Split(',').Select(x => byte.TryParse(x, out var value) ? (byte?)value : null).OfType<byte>().ToArray()
            };
        }).ToArray();
        // Conflicting unidentified services at one endpoint still need independent catalog keys.
        var collisions = physical.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < physical.Length; i++)
            if (collisions.Contains(physical[i].Id))
            {
                var source = OrderServices(groups[i]).First();
                var suffix = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.ServiceType + "|" + source.Instance)))[..16];
                physical[i] = physical[i] with { Id = $"endpoint:{physical[i].Id}#{suffix}" };
            }
        return physical.Where(r => r.StereoId.Length == 0).Concat(physical.Where(r => r.StereoId.Length > 0).GroupBy(r => r.StereoId).Select(group =>
        {
            var members = group.OrderByDescending(r => r.IsLeader).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
            var leader = members[0];
            return leader with { Id = $"stereo:{group.Key}", Name = leader.GroupName.Length > 0 ? leader.GroupName : leader.Name, Members = members, Aliases = [] };
        })).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static string Identity(ServiceRecord service)
    {
        if (service.Txt.TryGetValue("deviceid", out var id) && !string.IsNullOrWhiteSpace(id)) return id.Trim();
        var at = service.Instance.IndexOf('@');
        return service.ServiceType.StartsWith("_raop", StringComparison.Ordinal) && at > 0 ? service.Instance[..at].Trim() : "";
    }
    private static bool IsAirplay(ServiceRecord service) => service.ServiceType.StartsWith("_airplay", StringComparison.Ordinal);
    private static IOrderedEnumerable<ServiceRecord> OrderServices(IEnumerable<ServiceRecord> services) => services.OrderBy(s => IsAirplay(s) ? 0 : 1)
        .ThenBy(s => s.Instance, StringComparer.Ordinal).ThenBy(s => s.Address, StringComparer.Ordinal).ThenBy(s => s.Port);
    private static string Endpoint(ServiceRecord service) => ReceiverIdentity.Endpoint(service.Address, service.Port);
    private static string Model(ServiceRecord service) => Field([service], "model") is { Length: > 0 } model ? model : Field([service], "am");
    private static string Field(IEnumerable<ServiceRecord> services, string key) => services.Select(s => s.Txt.GetValueOrDefault(key, "").Trim()).FirstOrDefault(v => v.Length > 0) ?? "";
    private static bool Compatible(ServiceRecord left, ServiceRecord right) => !Conflicts(left, right).Any();
    private static IEnumerable<string> Conflicts(ServiceRecord left, ServiceRecord right)
    {
        if (!Matches(Model(left), Model(right), StringComparison.OrdinalIgnoreCase)) yield return "model";
        if (!Matches(Field([left], "tsid"), Field([right], "tsid"))) yield return "tsid";
        if (!Matches(Field([left], "pk"), Field([right], "pk"))) yield return "pk";
    }
    private static bool Matches(string left, string right, StringComparison comparison = StringComparison.Ordinal) => left.Length == 0 || right.Length == 0 || left.Equals(right, comparison);
    private static string Describe(ServiceRecord service) => System.Text.Json.JsonSerializer.Serialize(new { service.Instance, service.ServiceType, service.Address, service.Port, DeviceId = Identity(service), Model = Model(service), StereoId = Field([service], "tsid") });
}
public sealed record AudioEndpoint(string Id, string Name);
public sealed record DiscoveryAdapter(string Id, string Name, string Description, string Ipv4Addresses, bool IsUp, uint InterfaceIndex)
{
    public bool Available => IsUp && InterfaceIndex != 0 && Ipv4Addresses.Length > 0;
    public string Label => $"{Name} · {Ipv4Addresses} · {(Available ? L.Get("Connected") : L.Get("Unavailable"))}";
}
public static class DiscoveryInterface
{
    // An unavailable selection must never turn into index 0 (all interfaces).
    public static uint? ResolveIndex(string id, IEnumerable<DiscoveryAdapter> adapters)
    {
        if (id.Length == 0) return 0;
        var adapter = adapters.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return adapter is { Available: true } ? adapter.InterfaceIndex : null;
    }
}
public interface IDiscoveryService : IDisposable
{
    event Action<IReadOnlyList<Receiver>>? Changed;
    event Action<string>? Failed;
    void Start();
    void SetInterface(string id);
}
public interface IAudioService
{
    event Action? EndpointsChanged;
    Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync();
    Task<string?> GetDefaultEndpointIdAsync();
    Task MuteAsync(string? endpointId);
    Task RestoreAsync();
}
