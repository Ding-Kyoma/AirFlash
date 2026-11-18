namespace AirFlash.Core;

// Recovery ownership is independent of a playback generation or engine session.
// Implementations serialize journal/endpoint work away from the UI dispatcher.
public enum MuteJournalState { Intent, Owned, Restoring, Resolved, Abandoned }
public enum MuteRecoveryStatus { NotInitialized, Ready, Pending, Busy, Unreadable, Unrecognized }
public enum MuteRecoveryChoice { RestoreSaved, KeepCurrent }

public sealed record MuteRecoveryEntry(Guid Owner, long Generation, string EndpointId,
    bool PreviousMute, MuteJournalState State, bool? CurrentMute, bool Live, bool KnownRestored);

public sealed record MuteRecoverySnapshot(long Revision, MuteRecoveryStatus Status,
    IReadOnlyList<MuteRecoveryEntry> Entries, string? Detail = null)
{
    public bool CanMute => Status == MuteRecoveryStatus.Ready;
    public static MuteRecoverySnapshot NotInitialized { get; } = new(0, MuteRecoveryStatus.NotInitialized, []);
}

// Optional capability: existing fake/read-only audio services need no journal.
public interface IMuteRecoveryService
{
    event Action? RecoveryChanged;
    MuteRecoverySnapshot Recovery { get; }
    Task InitializeRecoveryAsync();
    Task RefreshRecoveryAsync();
    Task ResolveRecoveryAsync(long revision, Guid owner, long generation, string endpointId, MuteRecoveryChoice choice);
    Task ArchiveUnrecognizedRecoveryAsync(long revision);
}

public interface IMuteEndpoint : IDisposable
{
    string Id { get; }
    // Direction comes from the opened endpoint, never catalog metadata.
    AudioEndpointKind Kind { get; }
    bool Muted { get; set; }
}

public interface IMuteEndpointBackend
{
    // null resolves the actual current default endpoint, whose Id is journaled.
    IMuteEndpoint Open(string? endpointId);
}

public interface IMuteRecoveryJournal
{
    IDisposable AcquireLease();
    // Only genuine initial file absence returns null; every uncertain read throws.
    byte[]? Read();
    void Write(byte[] bytes);
    // Preserve exact unknown bytes before permitting a new empty journal.
    void Archive(byte[] expectedBytes);
}

public sealed class MuteRecoveryBusyException(string message, Exception? inner = null) : IOException(message, inner);
