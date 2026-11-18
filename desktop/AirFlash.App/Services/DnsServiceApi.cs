using AirFlash.Core;
namespace AirFlash.App.Services;

internal sealed record DnsAnnouncement(string Name, uint Ttl);
internal interface IDnsServiceOperation
{
    long Token { get; }
    string Name { get; }
    bool IsResolve { get; }
    bool IsSettled { get; }
    event Action? Settled;
    // Only asynchronously queued native cancellation failures use this channel.
    // A synchronous Cancel returns its status or throws to its caller instead.
    event Action<string>? CancellationFailed;
    event Action<string>? CallbackFailed;
    uint Start();
    uint Cancel();
}
internal interface IDnsServiceApi
{
    // Creation registers an owner but must not invoke callbacks until Start.
    IDnsServiceOperation CreateBrowse(string type, uint interfaceIndex, Action<uint, IReadOnlyList<DnsAnnouncement>> callback);
    IDnsServiceOperation CreateResolve(string name, string type, uint interfaceIndex, Action<uint, ServiceRecord?> callback);
}
// Shared across native browse owners and child processes, including old epochs.
// Tokens are never reused, and uncertain process exit retains its admission slot.
internal static class DnsServiceOwnership
{
    internal const int Limit = 128;
    private static readonly object Gate = new();
    private static long _nextToken;
    private static int _count;
    internal static int Count { get { lock (Gate) return _count; } }
    internal static long Acquire()
    {
        lock (Gate)
        {
            if (_count >= Limit) throw new DnsServiceCapacityException();
            if (_nextToken == long.MaxValue) throw new InvalidOperationException("DNS operation tokens are exhausted.");
            ++_count;
            return ++_nextToken;
        }
    }
    internal static void Release() { lock (Gate) --_count; }
}
internal enum DnsServiceCapacityReason { RetainedOwnership, ResolveConcurrency }
internal sealed class DnsServiceCapacityException : InvalidOperationException
{
    public DnsServiceCapacityReason Reason { get; }
    public DnsServiceCapacityException(DnsServiceCapacityReason reason = DnsServiceCapacityReason.RetainedOwnership)
        : base(reason == DnsServiceCapacityReason.ResolveConcurrency ? "The DNS resolve helper concurrency limit has been reached."
            : L.Get("Some discovery requests are paused because native DNS ownership cannot be safely released. Discovery resumes if ownership settles; restarting the app is the final recovery."))
    { Reason = reason; }
}
internal sealed record DiscoveryPolicy
{
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaximum { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxOwners { get; init; } = 64;
    public int MaxResolveOwnersPerName { get; init; } = 2;
}
internal sealed record DiscoverySnapshot(long Epoch, int AdvertisedNames, int CachedRecords, int PendingResolves, int NativeOwners, int ActiveQueries = 0);
