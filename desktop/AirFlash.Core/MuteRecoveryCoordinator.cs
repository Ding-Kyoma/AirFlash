using System.Text.Json;
namespace AirFlash.Core;

/// <summary>
/// Synchronous endpoint/journal state machine. Its service serializes calls on the
/// audio MTA; it never performs UI interaction. A lease proves current cooperation,
/// not that an endpoint has not been changed by the user after an earlier crash.
/// </summary>
public sealed class MuteRecoveryCoordinator(IMuteEndpointBackend backend, IMuteRecoveryJournal journal, Guid? owner = null) : IDisposable
{
    private const int MaximumBytes = 64 * 1024, MaximumEntries = 32, MaximumIdLength = 1024;
    private readonly Guid _owner = ValidateOwner(owner ?? Guid.NewGuid());
    private readonly List<Record> _records = [];
    private IDisposable? _lease;
    private byte[]? _expected, _unknown;
    private bool _hasExpected, _archivePending, _archiveConfirmed, _disposed;
    private long _generation, _revision;
    private string? _detail;
    private MuteRecoveryStatus? _blocked;
    private MuteRecoverySnapshot _snapshot = MuteRecoverySnapshot.NotInitialized;
    public MuteRecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;

    public void Initialize() => Refresh();

    public void Refresh()
    {
        ThrowIfDisposed();
        try
        {
            _lease ??= journal.AcquireLease();
            var bytes = journal.Read();
            if (_archivePending)
            {
                // An ambiguous archive completion or empty replacement cannot be
                // forgotten merely because a later read reports an absent file.
                _blocked = MuteRecoveryStatus.Unrecognized;
                Publish(); return;
            }
            if (!_hasExpected)
            {
                _expected = bytes?.ToArray(); _hasExpected = true;
                try { _records.AddRange(Parse(bytes)); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                {
                    _unknown = bytes?.ToArray();
                    _blocked = MuteRecoveryStatus.Unrecognized; _detail = error.Message;
                    Publish(); return;
                }
                _generation = _records.Select(record => record.Generation).DefaultIfEmpty(0).Max();
                _records.RemoveAll(record => IsTerminal(record.State));
            }
            else if (!Same(bytes, _expected))
            {
                _blocked = MuteRecoveryStatus.Unreadable;
                _detail = "Mute recovery journal changed outside its owner. Existing recovery information is preserved.";
                Publish(); return;
            }
            if (_unknown is not null)
            {
                _blocked = MuteRecoveryStatus.Unrecognized; Publish(); return;
            }
            _blocked = null; _detail = null;
            foreach (var record in _records.ToArray())
            {
                if (record.Settled) continue;
                try
                {
                    using var endpoint = OpenExact(record.EndpointId);
                    record.CurrentMute = endpoint.Muted;
                    if (!record.Live && record.CurrentMute == record.PreviousMute)
                    {
                        // Observing the saved bit needs no endpoint write. Persist
                        // settlement, and do not reapply it after later user changes.
                        record.State = MuteJournalState.Resolved;
                        record.Settled = record.KnownRestored = true;
                    }
                }
                catch (Exception error) { record.CurrentMute = null; _detail = error.Message; }
            }
            if (_records.Any(record => record.Settled)) TryPersist();
        }
        catch (MuteRecoveryBusyException error) { _blocked = MuteRecoveryStatus.Busy; _detail = error.Message; }
        catch (Exception error) { _blocked = MuteRecoveryStatus.Unreadable; _detail = error.Message; }
        Publish();
    }

    public void Mute(string? endpointId)
    {
        ThrowIfDisposed();
        try
        {
            using var endpoint = backend.Open(endpointId);
            ValidateId(endpoint.Id);
            if (endpointId is not null && endpoint.Id != endpointId)
                throw new InvalidOperationException("The endpoint backend returned a different endpoint identity.");
            // A recording endpoint is the source signal, not local speaker
            // output. Its actual direction must be known before mute or journal
            // access, including calls made before recovery initialization.
            if (EndpointKind(endpoint) == AudioEndpointKind.Recording) return;
            if (_records.Any(record => record.EndpointId == endpoint.Id && record.Live && record.State == MuteJournalState.Owned)) return;
            RequireReady();
            if (_records.Count >= MaximumEntries) throw new InvalidOperationException("Mute recovery already owns the maximum number of endpoints.");
            var previous = endpoint.Muted;
            var record = new Record(_owner, checked(++_generation), endpoint.Id, previous, MuteJournalState.Intent)
            { Live = true, CurrentMute = previous };
            // Reject an oversized new record before taking even in-memory
            // ownership. JSON escaping can exceed the raw identity-length bound.
            Serialize(_records.Append(record));
            _records.Add(record);
            try
            {
                Persist();
                // Establish memory ownership before a setter that may mutate and
                // then throw. The durable Intent already retains the exact bit.
                record.MutationAttempted = true;
                endpoint.Muted = true;
                record.CurrentMute = true; record.State = MuteJournalState.Owned;
                Persist();
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                if (!record.MutationAttempted)
                {
                    record.State = MuteJournalState.Resolved;
                    record.Settled = record.KnownRestored = true;
                    try { Persist(); } catch (Exception error) { errors.Add(error); }
                }
                else RestoreRecord(record, errors);
                _detail = failure.Message;
                throw new AggregateException("Mute acquisition failed; recovery information was retained or settled.", errors);
            }
        }
        finally { Publish(); }
    }

    public void Restore()
    {
        ThrowIfDisposed();
        var errors = new List<Exception>();
        try
        {
            foreach (var record in _records.Where(record => record.Live || record.Settled).ToArray())
                RestoreRecord(record, errors);
            if (errors.Count > 0)
            {
                _detail = errors[0].Message;
                throw new AggregateException("Some mute recovery operations could not complete.", errors);
            }
            _detail = null;
        }
        finally { Publish(); }
    }

    private void RestoreRecord(Record record, List<Exception> errors)
    {
        if (record.Settled)
        {
            try { Persist(); } catch (Exception error) { errors.Add(error); }
            return;
        }
        record.State = MuteJournalState.Restoring;
        try { Persist(); } catch (Exception error) { errors.Add(error); }
        // Normal cleanup still attempts the exact bit after journal failure. It
        // does not depend on a successfully persisted Restoring transition.
        try
        {
            using var endpoint = OpenExact(record.EndpointId);
            endpoint.Muted = record.PreviousMute;
            record.CurrentMute = record.PreviousMute;
            record.State = MuteJournalState.Resolved;
            record.Settled = record.KnownRestored = true;
        }
        catch (Exception error) { record.CurrentMute = null; errors.Add(error); return; }
        try { Persist(); } catch (Exception error) { errors.Add(error); }
    }

    public void Resolve(long revision, Guid owner, long generation, string endpointId, MuteRecoveryChoice choice)
    {
        ThrowIfDisposed();
        try
        {
            if (revision != Snapshot.Revision) throw new InvalidOperationException("Mute recovery changed. Refresh and review the current state.");
            if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
            var record = _records.SingleOrDefault(record => record.Owner == owner && record.Generation == generation && record.EndpointId == endpointId)
                ?? throw new InvalidOperationException("The selected mute recovery owner no longer exists.");
            if (record.Settled) { Persist(); return; }
            if (_blocked is not null || !record.CurrentMute.HasValue) throw new InvalidOperationException("The endpoint or journal is unavailable. Refresh before deciding.");
            var shownMute = record.CurrentMute;
            record.CurrentMute = null;
            using var endpoint = OpenExact(endpointId);
            var current = endpoint.Muted;
            record.CurrentMute = current;
            if (current != shownMute)
            {
                throw new InvalidOperationException("The endpoint mute bit changed after it was shown. Review the new state before deciding.");
            }
            VerifyUnchanged();
            if (choice == MuteRecoveryChoice.KeepCurrent)
            {
                record.State = MuteJournalState.Abandoned; record.Settled = true;
            }
            else
            {
                record.State = MuteJournalState.Restoring;
                Persist();
                record.MutationAttempted = true;
                try { endpoint.Muted = record.PreviousMute; }
                catch { record.CurrentMute = null; throw; }
                record.CurrentMute = record.PreviousMute;
                record.State = MuteJournalState.Resolved;
                record.Settled = record.KnownRestored = true;
            }
            Persist(); _detail = null;
        }
        catch (Exception error) { _detail = error.Message; throw; }
        finally { Publish(); }
    }

    public void ArchiveUnrecognized(long revision)
    {
        ThrowIfDisposed();
        try
        {
            if (revision != Snapshot.Revision || _unknown is null || _lease is null)
                throw new InvalidOperationException("The unrecognized journal changed. Refresh before archiving it.");
            _archivePending = true;
            if (!_archiveConfirmed)
            {
                VerifyUnchanged();
                journal.Archive(_unknown);
                _archiveConfirmed = true;
                _expected = null;
            }
            _records.Clear();
            Persist();
            _unknown = null; _archivePending = _archiveConfirmed = false;
            _blocked = null; _detail = null;
        }
        catch (Exception error) { _blocked = MuteRecoveryStatus.Unrecognized; _detail = error.Message; throw; }
        finally { Publish(); }
    }

    private void TryPersist()
    {
        try { Persist(); } catch (Exception error) { _detail = error.Message; }
    }
    private void Persist()
    {
        VerifyUnchanged();
        var bytes = Serialize(_records);
        try { journal.Write(bytes); }
        catch
        {
            // A failed final flush can follow a successful replacement. Adopt only
            // our exact intended bytes as the new comparison baseline, without
            // claiming persistence succeeded or losing in-memory settlement.
            try { if (Same(journal.Read(), bytes)) _expected = bytes; } catch { }
            throw;
        }
        _expected = bytes; _hasExpected = true;
        _records.RemoveAll(record => record.Settled && IsTerminal(record.State));
    }
    private static byte[] Serialize(IEnumerable<Record> records)
    {
        var entries = records.ToArray();
        var bytes = SerializeEntries(entries);
        // Successful exact restoration may settle entries whose longer state
        // names exceed the cap. Removing only settled records is equivalent to
        // their durable resolution and retains every unresolved original bit.
        if (bytes.Length > MaximumBytes && entries.Any(record => record.Settled))
            bytes = SerializeEntries(entries.Where(record => !record.Settled));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Mute recovery journal exceeds 64 KiB.");
        return bytes;
    }
    private static byte[] SerializeEntries(IEnumerable<Record> records)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            entries = records.Select(record => new
            {
                owner = record.Owner, generation = record.Generation, endpoint_id = record.EndpointId,
                previous_mute = record.PreviousMute, state = record.State.ToString()
            })
        });
    private void VerifyUnchanged()
    {
        if (_lease is null || !_hasExpected) throw new InvalidOperationException("Mute recovery has not acquired and read its journal.");
        var bytes = journal.Read();
        if (!Same(bytes, _expected))
        {
            _blocked = MuteRecoveryStatus.Unreadable;
            throw new IOException("Mute recovery journal changed outside its owner. Existing bytes will not be overwritten.");
        }
    }
    private void RequireReady()
    {
        if (!Snapshot.CanMute) throw new InvalidOperationException("Resolve pending mute recovery before starting another muted stream.");
    }
    private IMuteEndpoint OpenExact(string id)
    {
        var endpoint = backend.Open(id);
        try
        {
            if (endpoint.Id != id) throw new InvalidOperationException("The endpoint backend returned a different endpoint identity.");
            ValidateId(endpoint.Id);
            if (EndpointKind(endpoint) != AudioEndpointKind.Playback)
                throw new InvalidOperationException("Mute recovery requires the saved playback endpoint; recording devices are never muted.");
            return endpoint;
        }
        catch { endpoint.Dispose(); throw; }
    }
    private static AudioEndpointKind EndpointKind(IMuteEndpoint endpoint)
    {
        var kind = endpoint.Kind;
        if (!Enum.IsDefined(kind)) throw new InvalidOperationException("The endpoint direction is unsupported.");
        return kind;
    }
    private void Publish()
    {
        var entries = _records.Select(record => new MuteRecoveryEntry(record.Owner, record.Generation,
            record.EndpointId, record.PreviousMute, record.State, record.CurrentMute, record.Live, record.KnownRestored)).ToArray();
        var status = _blocked ?? (!_hasExpected ? MuteRecoveryStatus.NotInitialized
            : entries.Any(entry => !entry.Live || entry.State != MuteJournalState.Owned || entry.CurrentMute is null)
                ? MuteRecoveryStatus.Pending : MuteRecoveryStatus.Ready);
        Volatile.Write(ref _snapshot, new(checked(++_revision), status, Array.AsReadOnly(entries), _detail));
        Changed?.Invoke();
    }
    private static List<Record> Parse(byte[]? bytes)
    {
        if (bytes is null) return [];
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Mute recovery journal exceeds 64 KiB.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        RequireProperties(root, ["version", "entries"]);
        var versionValue = root.GetProperty("version");
        if (versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Mute recovery journal version is unsupported.");
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumEntries)
            throw new InvalidDataException("Mute recovery journal entries are invalid or exceed the supported limit.");
        var result = new List<Record>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<(Guid, long)>();
        foreach (var entry in entries.EnumerateArray())
        {
            RequireProperties(entry, ["owner", "generation", "endpoint_id", "previous_mute", "state"]);
            var ownerValue = entry.GetProperty("owner");
            var generationValue = entry.GetProperty("generation");
            var idValue = entry.GetProperty("endpoint_id");
            var previous = entry.GetProperty("previous_mute");
            var stateValue = entry.GetProperty("state");
            if (ownerValue.ValueKind != JsonValueKind.String || !Guid.TryParse(ownerValue.GetString(), out var owner) || owner == Guid.Empty
                || generationValue.ValueKind != JsonValueKind.Number || !generationValue.TryGetInt64(out var generation) || generation <= 0
                || idValue.ValueKind != JsonValueKind.String || previous.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || stateValue.ValueKind != JsonValueKind.String || !Enum.TryParse<MuteJournalState>(stateValue.GetString(), out var state)
                || !Enum.IsDefined(state) || stateValue.GetString() != state.ToString())
                throw new InvalidDataException("Mute recovery journal contains an invalid ownership entry.");
            var id = idValue.GetString()!; ValidateId(id);
            if (!ids.Add(id) || !owners.Add((owner, generation))) throw new InvalidDataException("Mute recovery journal contains duplicate ownership entries.");
            result.Add(new(owner, generation, id, previous.GetBoolean(), state));
        }
        return result;
    }
    private static void RequireProperties(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Mute recovery journal contains a non-object record.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Mute recovery journal contains unknown or duplicate fields.");
        if (seen.Count != names.Length) throw new InvalidDataException("Mute recovery journal has missing fields.");
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumIdLength)
            throw new InvalidDataException("Mute recovery endpoint identity is invalid or too long.");
    }
    private static Guid ValidateOwner(Guid owner)
        => owner != Guid.Empty ? owner : throw new ArgumentException("Mute recovery owner must not be empty.", nameof(owner));
    private static bool IsTerminal(MuteJournalState state) => state is MuteJournalState.Resolved or MuteJournalState.Abandoned;
    private static bool Same(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease?.Dispose(); _lease = null;
    }
    private sealed class Record(Guid owner, long generation, string endpointId, bool previousMute, MuteJournalState state)
    {
        public readonly Guid Owner = owner;
        public readonly long Generation = generation;
        public readonly string EndpointId = endpointId;
        public readonly bool PreviousMute = previousMute;
        public MuteJournalState State = state;
        public bool? CurrentMute;
        public bool Live, MutationAttempted, KnownRestored, Settled;
    }
}
