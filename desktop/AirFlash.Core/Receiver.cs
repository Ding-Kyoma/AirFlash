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
    public bool IsGroup => Members.Length > 0 || Id.StartsWith("stereo:", StringComparison.Ordinal);
    public bool Complete => !IsGroup || Members.Length == 2;
    public string Detail => IsGroup ? L.Format("HomePod stereo · {0}/2", Members.Length) : Model.Length > 0 ? Model : L.Get("AirFlash receiver");
    [JsonIgnore] public IEnumerable<Receiver> Peers => IsGroup ? Members : [this];
    public string TransportKey => string.Join(";", Peers.Select(p => $"{p.Address}:{p.Port}:{p.IsLeader}"));
    public static string PhysicalKey(string identity, string address) => string.IsNullOrWhiteSpace(identity) ? address : identity.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
}
public sealed record ServiceRecord(string Instance, string ServiceType, string Address, int Port, IReadOnlyDictionary<string, string> Txt);
public static class ReceiverAggregator
{
    public static IReadOnlyList<Receiver> Build(IEnumerable<ServiceRecord> services)
    {
        var physical = services.GroupBy(s => Receiver.PhysicalKey(Identity(s), s.Address)).Select(group =>
        {
            var service = group.OrderBy(s => s.ServiceType.StartsWith("_airplay", StringComparison.Ordinal) ? 0 : 1).First();
            var txt = service.Txt;
            var name = service.Instance.Split("._", StringSplitOptions.None)[0];
            if (name.Contains('@')) name = name[(name.IndexOf('@') + 1)..];
            var identity = Identity(service);
            var codecText = group.FirstOrDefault(s => s.ServiceType.StartsWith("_raop", StringComparison.Ordinal))?.Txt.GetValueOrDefault("cn", "") ?? "";
            return new Receiver(identity.Length > 0 ? identity : $"{service.Address}:{service.Port}", name, service.Address, service.Port)
            {
                DeviceId = identity,
                Model = txt.GetValueOrDefault("model", txt.GetValueOrDefault("am", "")),
                StereoId = txt.GetValueOrDefault("tsid", ""),
                GroupName = txt.GetValueOrDefault("gpn", ""),
                IsLeader = txt.GetValueOrDefault("igl") == "1",
                Codecs = codecText.Split(',').Select(x => byte.TryParse(x, out var value) ? (byte?)value : null).OfType<byte>().ToArray()
            };
        }).ToArray();
        return physical.Where(r => r.StereoId.Length == 0).Concat(physical.Where(r => r.StereoId.Length > 0).GroupBy(r => r.StereoId).Select(group =>
        {
            var members = group.OrderByDescending(r => r.IsLeader).ThenBy(r => r.DeviceId, StringComparer.Ordinal).ToArray();
            var leader = members[0];
            return leader with { Id = $"stereo:{group.Key}", Name = leader.GroupName.Length > 0 ? leader.GroupName : leader.Name, Members = members };
        })).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static string Identity(ServiceRecord service)
    {
        if (service.Txt.TryGetValue("deviceid", out var id)) return id;
        var at = service.Instance.IndexOf('@');
        return service.ServiceType.StartsWith("_raop", StringComparison.Ordinal) && at > 0 ? service.Instance[..at] : "";
    }
}
public sealed record AudioEndpoint(string Id, string Name);
public interface IDiscoveryService : IDisposable
{
    event Action<IReadOnlyList<Receiver>>? Changed;
    event Action<string>? Failed;
    void Start();
}
public interface IAudioService
{
    event Action? EndpointsChanged;
    Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync();
    Task<string?> GetDefaultEndpointIdAsync();
    Task MuteAsync(string? endpointId);
    Task RestoreAsync();
}
