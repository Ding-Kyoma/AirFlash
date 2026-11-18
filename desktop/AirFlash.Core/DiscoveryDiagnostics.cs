using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AirFlash.Core;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryDemand>))]
public enum DiscoveryDemand { None = 0, Panel = 1, Receivers = 2, Startup = 4, SoftwareStereo = 8 }
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryTraceEvent>))]
public enum DiscoveryTraceEvent { DemandChanged, Started, Stopped, Restarted, AdvertisementAdded, AdvertisementRemoved, CachedRecordTtlRefreshed, ResolveSucceeded, ResolveFailed, RecordRemoved, BrowseFailed, Unavailable, Disposed }
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryTraceReason>))]
public enum DiscoveryTraceReason { None, Requested, InterfaceChanged, NetworkAddressChanged, NetworkAvailabilityChanged, AdapterChanged, AdapterLookupFailed, PtrGoodbye, PtrExpired, ResumeGraceExpired, CacheExpired, ResolveTimeout, ResolveFailed, DnsUnavailable, QueryStartFailed, QueryEndedUnexpectedly }
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryServiceType>))]
public enum DiscoveryServiceType { Unknown, Airplay, Raop }

/// <summary>Anonymous correlations are local to one discovery instance. They do not establish receiver ownership.</summary>
public sealed record DiscoveryTraceEntry(long Sequence, DateTimeOffset ReceivedAt, long ElapsedMs, long Epoch,
    DiscoveryTraceEvent Event, DiscoveryTraceReason Reason, DiscoveryDemand Demand, int CachedRecords,
    DiscoveryServiceType ServiceType, string? ServiceToken, string? DeviceToken, string? StereoToken,
    string? EndpointToken, bool? StereoMetadataPresent, uint? TtlSeconds);

public sealed record DiscoveryDiagnosticsSnapshot(long Epoch, DiscoveryDemand Demand, bool Started, bool Running,
    bool? InterfaceAvailable, int AdvertisedNames, int CachedRecords, int PendingResolves, int NativeOwners,
    long RecordedEvents, long OverwrittenEvents, IReadOnlyList<DiscoveryTraceEntry> History)
{
    public const int HistoryCapacity = 128;
    public bool Disposed { get; init; }
}

/// <summary>Optional observation only; implementations must not start discovery or contact receivers to produce a snapshot.</summary>
public interface IDiscoveryDiagnostics
{
    void RecordDemand(DiscoveryDemand demand);
    DiscoveryDiagnosticsSnapshot SnapshotDiagnostics();
}

// The coordinator serializes access. This stores no names, addresses, TXT records,
// or lookup maps; even correlations are keyed differently in each instance.
internal sealed class DiscoveryHistory
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Queue<DiscoveryTraceEntry> _entries = new();
    public long Recorded { get; private set; }
    public long Overwritten => Recorded - _entries.Count;
    public DiscoveryTraceEntry[] Snapshot() => _entries.ToArray();

    public void Record(DateTimeOffset receivedAt, TimeSpan elapsed, long epoch, DiscoveryTraceEvent kind,
        DiscoveryTraceReason reason, DiscoveryDemand demand, int cachedRecords, string? name = null,
        string? type = null, ServiceRecord? record = null, uint? ttl = null, bool identityConflict = false)
    {
        name ??= record?.Instance;
        type ??= record?.ServiceType;
        var identity = record is null || identityConflict ? "" : ReceiverIdentity.Normalize(ReceiverAggregator.Identity(record));
        var stereo = record?.Txt.GetValueOrDefault("tsid", "").Trim();
        var serviceType = type?.TrimEnd('.') switch
        {
            { } value when value.Equals("_airplay._tcp.local", StringComparison.OrdinalIgnoreCase) => DiscoveryServiceType.Airplay,
            { } value when value.Equals("_raop._tcp.local", StringComparison.OrdinalIgnoreCase) => DiscoveryServiceType.Raop,
            _ => DiscoveryServiceType.Unknown
        };
        _entries.Enqueue(new(++Recorded, receivedAt, elapsed.Ticks / TimeSpan.TicksPerMillisecond, epoch, kind,
            reason, demand, cachedRecords, serviceType,
            string.IsNullOrEmpty(name) ? null : Token("service", name.TrimEnd('.').ToUpperInvariant()),
            identity.Length == 0 ? null : Token("device", identity),
            string.IsNullOrEmpty(stereo) ? null : Token("stereo", stereo),
            record is null ? null : Token("endpoint", ReceiverIdentity.Endpoint(record.Address, record.Port)),
            record is null ? null : !string.IsNullOrEmpty(stereo), ttl));
        if (_entries.Count > DiscoveryDiagnosticsSnapshot.HistoryCapacity) _entries.Dequeue();
    }

    private string Token(string domain, string value) => Convert.ToHexString(
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(domain + "\0" + value))).ToLowerInvariant();
}
