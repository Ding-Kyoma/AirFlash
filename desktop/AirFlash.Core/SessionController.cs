using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace AirFlash.Core;

public enum PlaybackState { Idle, Connecting, Streaming, Standby, Pairing, Error }
public sealed record StreamMetrics(double? CaptureToSendP95 = null, double? MaxQueueAge = null, long? Underruns = null, long? DroppedFrames = null, int? InputRate = null);
public sealed record SessionSnapshot(PlaybackState State, Receiver? Receiver = null, string Message = "", StreamMetrics? Metrics = null, int TargetLatency = 0, PlaybackDiagnostics? Diagnostics = null, int StreamRate = 0, DeviceVolumeState? DeviceVolume = null, IReadOnlyDictionary<string, DeviceVolumeState>? MemberVolumes = null)
{
    public bool IsActive => State is PlaybackState.Connecting or PlaybackState.Streaming or PlaybackState.Standby or PlaybackState.Pairing;
    internal long Generation { get; init; }
}
public sealed record SessionTiming(TimeSpan Connect, TimeSpan Read, TimeSpan Pin, TimeSpan Retry)
{
    public static SessionTiming Default { get; } = new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1));
}
public sealed class SessionController(IEngineFactory factory, IAudioService audio, Action<string>? log = null, SessionTiming? timing = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly SemaphoreSlim _audioWriter = new(1, 1);
    private readonly object _sync = new();
    private readonly SessionTiming _timing = timing ?? SessionTiming.Default;
    private CancellationTokenSource? _lifetime;
    private Task? _work;
    private IEngineConnection? _connection;
    private string? _sessionId;
    private AppSettings _settings = new();
    private Receiver? _receiver;
    private Receiver? _discoveredReceiver;
    private long _generation;
    private bool _muted;
    private bool _transportStreaming;
    private readonly SemaphoreSlim _equalizerWriter = new(1, 1);
    private EqualizerSettings? _equalizerPreview;
    private Guid? _equalizerOwner;
    private long _equalizerSequence;
    private CancellationTokenSource? _equalizerEdit;
    private Task? _equalizerEditTask;
    private bool _equalizerReady, _equalizerDisposed;
    private (bool Enabled, double Seconds)? _sentStandby;
    public event Action<string?>? EqualizerFeedback;
    public Task PreviewEqualizerAsync(Guid owner, EqualizerSettings equalizer)
    {
        if (equalizer.Validate() is { } error) throw new ArgumentException(error, nameof(equalizer));
        lock (_sync)
        {
            if (_equalizerDisposed) return Task.CompletedTask;
            if (_equalizerOwner != owner) { _equalizerEdit?.Cancel(); _equalizerEdit = null; _equalizerEditTask = null; }
            _equalizerOwner = owner; _equalizerPreview = equalizer.Clone(); ++_equalizerSequence;
            if (_equalizerEdit is not null) return _equalizerEditTask!;
            _equalizerEdit = new();
            return _equalizerEditTask = PreviewEqualizerLoopAsync(_equalizerEdit);
        }
    }
    private async Task PreviewEqualizerLoopAsync(CancellationTokenSource edit)
    {
        var token = edit.Token;
        try
        {
            while (true)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
                long sequence;
                lock (_sync) sequence = _equalizerSequence;
                await SendEqualizerAsync(sequence, token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!ReferenceEquals(_equalizerEdit, edit)) return;
                    if (sequence != _equalizerSequence) continue;
                    _equalizerEdit = null; _equalizerEditTask = null; return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_equalizerEdit, edit)) { _equalizerEdit = null; _equalizerEditTask = null; } }
            edit.Dispose();
        }
    }
    public async Task ClearEqualizerPreviewAsync(Guid owner)
    {
        long sequence;
        lock (_sync)
        {
            if (_equalizerDisposed || _equalizerOwner != owner) return;
            _equalizerEdit?.Cancel(); _equalizerEdit = null; _equalizerEditTask = null;
            _equalizerOwner = null; _equalizerPreview = null; sequence = ++_equalizerSequence;
        }
        await SendEqualizerAsync(sequence, CancellationToken.None).ConfigureAwait(false);
    }
    private EqualizerSettings EffectiveEqualizer() { lock (_sync) return (_equalizerPreview ?? _settings.Equalizer).Clone(); }
    private async Task SendEqualizerAsync(long? expectedSequence = null, CancellationToken cancellation = default)
    {
        await _equalizerWriter.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            IEngineConnection? connection; string? session; EqualizerSettings equalizer; long sequence, epoch;
            lock (_sync)
            {
                if (_equalizerDisposed || !_equalizerReady || expectedSequence is { } expected && expected != _equalizerSequence) return;
                connection = _connection; session = _sessionId; epoch = _generation;
                equalizer = (_equalizerPreview ?? _settings.Equalizer).Clone(); sequence = _equalizerSequence;
                if (sequence == 0) sequence = ++_equalizerSequence;
            }
            if (connection is null || session is null) return;
            try { await connection.SendAsync(session, "set_equalizer", new { sequence, equalizer = equalizer.WireParameters() }, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_sync) { if (epoch != _generation || sequence != _equalizerSequence) return; }
                log?.Invoke($"Equalizer update deferred: {error.Message}"); EqualizerFeedback?.Invoke(error.Message);
            }
        }
        finally { _equalizerWriter.Release(); }
    }
    private DeviceVolumeState _deviceVolume = new();
    private readonly Dictionary<string, DeviceVolumeState> _memberVolumes = [];
    private readonly Dictionary<string, string> _memberHosts = [];
    private long _volumeSequence;
    private CancellationTokenSource? _volumeEdit;
    public async Task SetDeviceVolumeAsync(string receiverId, int value)
    {
        if (Snapshot.Receiver?.IsSoftwareGroup == true) { await SetMemberVolumeAsync(receiverId, value).ConfigureAwait(false); return; }
        CancellationToken token;
        long sequence, epoch;
        lock (_sync)
        {
            if (_receiver?.Id != receiverId || !_deviceVolume.Available || _snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby)) return;
            _volumeEdit?.Cancel(); _volumeEdit?.Dispose();
            _volumeEdit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
            token = _volumeEdit.Token; sequence = ++_volumeSequence; epoch = _generation;
            _deviceVolume = _deviceVolume.Begin(Math.Clamp(value, 0, 100), sequence);
        }
        PublishCurrent(epoch);
        try
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                IEngineConnection? connection; string? session;
                lock (_sync)
                {
                    if (epoch != _generation || sequence != _deviceVolume.Sequence) return;
                    connection = _connection; session = _sessionId;
                }
                if (connection is null || session is null) return;
                await connection.SendAsync(session, "set_device_volume", new { volume = Math.Clamp(value, 0, 100), sequence }, token).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            lock (_sync)
            {
                if (epoch != _generation || sequence != _deviceVolume.Sequence || _deviceVolume.Target is null) return;
                _deviceVolume = _deviceVolume.Timeout();
            }
            PublishCurrent(epoch);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            lock (_sync)
            {
                if (epoch != _generation || sequence != _deviceVolume.Sequence) return;
                _deviceVolume = _deviceVolume.Timeout() with { Available = false };
            }
            log?.Invoke($"Device volume command failed: {error.Message}");
            PublishCurrent(epoch);
        }
    }
    private void PublishCurrent(long epoch)
    {
        // Hold the state lock through publication so an old state cannot replace stop/reconnect.
        lock (_sync) { if (epoch == _generation) Publish(epoch, _snapshot.State, _snapshot.Message); }
    }
    private PlaybackDiagnostics _diagnostics = new();
    private async Task SetMemberVolumeAsync(string memberId, int value)
    {
        long epoch, sequence; string host, session; IEngineConnection connection; CancellationToken token;
        lock (_sync)
        {
            if (_receiver?.IsSoftwareGroup != true || _snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby) || !_memberVolumes.TryGetValue(memberId, out var state) || !state.Available || !_memberHosts.TryGetValue(memberId, out host!) || _connection is null || _sessionId is null) return;
            epoch = _generation; sequence = ++_volumeSequence; connection = _connection; session = _sessionId; token = _lifetime!.Token;
            _memberVolumes[memberId] = state.Begin(Math.Clamp(value, 0, 100), sequence);
        }
        PublishCurrent(epoch);
        try
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            lock (_sync) { if (epoch != _generation || _memberVolumes.GetValueOrDefault(memberId)?.Sequence != sequence) return; }
            await connection.SendAsync(session, "set_device_volume", new { host, volume = Math.Clamp(value, 0, 100), sequence }, token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            lock (_sync) { if (epoch != _generation || _memberVolumes.GetValueOrDefault(memberId)?.Sequence != sequence || _memberVolumes[memberId].Target is null) return; _memberVolumes[memberId] = _memberVolumes[memberId].Timeout(); }
            PublishCurrent(epoch);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log?.Invoke($"Member volume command failed: {error.Message}");
            lock (_sync) { if (epoch != _generation || !_memberVolumes.TryGetValue(memberId, out var state) || state.Sequence != sequence) return; _memberVolumes[memberId] = state.Timeout() with { Available = false }; }
            PublishCurrent(epoch);
        }
    }
    private readonly Dictionary<string, EngineNotice> _warnings = [];
    private SessionSnapshot _snapshot = new(PlaybackState.Idle);
    public SessionSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public event Action<SessionSnapshot>? Changed;
    public bool Muted => _muted;
    private static ReceiverOptions ProfileOptions(Receiver receiver, Receiver member, AppSettings settings) => settings.ReadOptions(receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.Id : member.Id);
    private static object PeerProfile(Receiver receiver, Receiver member, AppSettings settings)
    {
        var options = ProfileOptions(receiver, member, settings);
        return new { codec = options.Codec, transport = options.Transport, timing = options.Timing == "follow" ? "ptp" : options.Timing,
            follow_receiver_clock = options.Timing == "follow" || options.Timing == "auto" && member.FollowReceiverClock, features = member.Features,
            channel = receiver.IsSoftwareGroup ? member.Channel : "stereo", sync_offset_ms = receiver.IsSoftwareGroup ? member.SyncOffsetMs : 0,
            latency_ms = settings.Latency(receiver.IsSoftwareGroup ? member.Id : receiver.Id) };
    }
    private static Dictionary<string, string?> ConnectionFields(Receiver receiver, AppSettings settings)
    {
        var application = settings.CaptureMode == "application";
        return new()
        {
            ["receiver"] = JsonSerializer.Serialize(new { mode = receiver.IsSoftwareGroup ? "software_stereo" : receiver.IsGroup ? "native_stereo" : "single",
                group = receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.StereoId : null,
                peers = receiver.Peers.OrderBy(m => m.Address, StringComparer.Ordinal).ThenBy(m => m.Port).Select(m => new { m.Address, m.Port, leader = receiver.IsGroup && m.IsLeader }) }),
            ["capture"] = JsonSerializer.Serialize(new { settings.CaptureMode, endpoint = settings.EffectiveEndpoint,
                path = application ? settings.CaptureApplicationPath : null, process = application ? settings.CaptureApplicationInstance?.ProcessId : null,
                started = application ? settings.CaptureApplicationInstance?.StartedAt : null }),
            ["latency"] = settings.Latency(receiver.Id).ToString(),
            ["sample_rate"] = settings.SampleRate(receiver),
            ["profiles"] = JsonSerializer.Serialize(receiver.Peers.OrderBy(m => m.Address, StringComparer.Ordinal).ThenBy(m => m.Port).Select(m => PeerProfile(receiver, m, settings)))
        };
    }
    private static string[] ConnectionChanges(Receiver previous, AppSettings old, Receiver receiver, AppSettings settings)
    {
        var before = ConnectionFields(previous, old); var after = ConnectionFields(receiver, settings);
        return before.Keys.Where(key => before[key] != after[key]).ToArray();
    }
    private Receiver RestartReceiver(Receiver receiver, AppSettings settings)
    {
        var next = _discoveredReceiver is { Online: true, Complete: true } discovered && discovered.Id == receiver.Id ? discovered : receiver;
        return next.IsSoftwareGroup ? SoftwareStereo.Resolve(next.Members.Concat(settings.ManualReceivers.Select(m => new Receiver(m.Id, m.Name, m.Host, m.Port) { IsManual = true })), settings).First(g => g.Id == next.Id) : next;
    }
    public async Task StartAsync(Receiver receiver, AppSettings settings)
    {
        if (!receiver.Complete) throw new InvalidOperationException(L.Get("Both stereo pair members must be online."));
        if (settings.Validate() is { } validation) throw new ArgumentException(validation, nameof(settings));
        if (receiver.IsSoftwareGroup && (!receiver.Online || receiver.Members.Select(m => Receiver.PhysicalKey(m.DeviceId, m.Address)).Distinct().Count() != 2)) throw new InvalidOperationException(L.Get("Stereo groups require two distinct independent receivers."));
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await StartLockedAsync(receiver, settings, null, "user_play").ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    public async Task PairAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>> requestPin)
    {
        if (!receiver.Complete) throw new InvalidOperationException(L.Get("Wait until both stereo pair members are online before pairing."));
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await StartLockedAsync(receiver, settings, requestPin, "user_pair").ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    private async Task StartLockedAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>>? requestPin, string reason, string[]? changedFields = null)
    {
        log?.Invoke($"Session start reason={reason}; changed_fields={string.Join(",", changedFields ?? [])}");
        await StopLockedAsync().ConfigureAwait(false);
        _settings = settings.Clone(); _receiver = receiver;
        var epoch = ++_generation;
        lock (_sync) { _diagnostics = new(); _warnings.Clear(); _snapshot = new(PlaybackState.Idle); }
        _lifetime = new();
        var cancellation = _lifetime.Token;
        Publish(epoch, requestPin is null ? PlaybackState.Connecting : PlaybackState.Pairing);
        _work = Task.Run(async () =>
        {
            try
            {
                if (requestPin is not null) await PairMembersAsync(receiver, requestPin, epoch, cancellation).ConfigureAwait(false);
                await RunStreamAsync(receiver, epoch, cancellation).ConfigureAwait(false);
            }
            catch (PairCancelledException) { }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { if (RecordFailure(epoch, error) is { } notice) Publish(epoch, PlaybackState.Error, notice.Detail); }
            finally { await RestoreAudioAsync().ConfigureAwait(false); }
        }, CancellationToken.None);
    }
    public async Task StopAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { log?.Invoke("Session stop reason=user_stop"); await StopLockedAsync().ConfigureAwait(false); _receiver = null; Publish(_generation, PlaybackState.Idle); }
        finally { _serial.Release(); }
    }
    private async Task StopLockedAsync()
    {
        lock (_sync) { ++_generation; _discoveredReceiver = null; _volumeEdit?.Cancel(); _volumeEdit?.Dispose(); _volumeEdit = null; _deviceVolume = new(); _memberVolumes.Clear(); _memberHosts.Clear(); }
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_work is not null) await _work.ConfigureAwait(false);
        _lifetime?.Dispose(); _lifetime = null; _work = null;
        await RestoreAudioAsync().ConfigureAwait(false);
    }
    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _settings;
            if (_receiver is { IsSoftwareGroup: true } group && settings.SoftwareGroups.All(g => g.Id != group.Id))
            { await StopLockedAsync().ConfigureAwait(false); _receiver = null; _settings = settings.Clone(); Publish(_generation, PlaybackState.Idle); return; }
            var previous = _receiver;
            if (_receiver is { IsSoftwareGroup: true } software)
            {
                var catalog = software.Members.Concat(settings.ManualReceivers.Select(m => new Receiver(m.Id, m.Name, m.Host, m.Port) { IsManual = true }));
                var resolved = SoftwareStereo.Resolve(catalog, settings).First(g => g.Id == software.Id);
                if (!resolved.Complete || !resolved.Online)
                {
                    await StopLockedAsync().ConfigureAwait(false); _receiver = null; _settings = settings.Clone();
                    Publish(_generation, PlaybackState.Error, L.Get("Stereo groups require two distinct independent receivers.")); return;
                }
                _receiver = resolved;
            }
            _settings = settings.Clone();
            lock (_sync) ++_equalizerSequence;
            if (_receiver is { } receiver && Snapshot.IsActive && Snapshot.State != PlaybackState.Pairing && ConnectionChanges(previous ?? receiver, old, receiver, settings) is { Length: > 0 } changes)
                await StartLockedAsync(RestartReceiver(receiver, settings), settings, null, "settings_change", changes).ConfigureAwait(false);
            else
            {
                await ApplyLocalMuteAsync().ConfigureAwait(false);
                await SendGainAsync().ConfigureAwait(false);
                await SendStandbyAsync().ConfigureAwait(false);
                await SendEqualizerAsync().ConfigureAwait(false);
            }
        }
        finally { _serial.Release(); }
    }
    public async Task UpdateReceiverAsync(Receiver receiver, AppSettings settings)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_receiver is not { } previous) return;
            if (Snapshot.IsActive && Snapshot.State != PlaybackState.Pairing && ConnectionChanges(previous, _settings, receiver, settings) is { Length: > 0 } changes)
                await StartLockedAsync(receiver, settings, null, "settings_change", changes).ConfigureAwait(false);
            else
            {
                lock (_sync)
                {
                    RemapReceiverIdentities(previous, receiver);
                    _receiver = receiver; _settings = settings.Clone();
                }
                await SendStandbyAsync().ConfigureAwait(false);
                PublishCurrent(_generation);
            }
        }
        finally { _serial.Release(); }
    }
    private bool OwnsDiscoverySnapshot(SessionSnapshot expected) => expected.Generation == _generation && expected.Receiver?.Id == _receiver?.Id
        && _receiver is not null && _snapshot.State is not (PlaybackState.Idle or PlaybackState.Pairing);
    private void RemapReceiverIdentities(Receiver previous, Receiver receiver)
    {
        var identities = previous.Peers.Select(old => (Old: old, Current: receiver.Peers.FirstOrDefault(p => p.Address == old.Address && p.Port == old.Port)))
            .Where(p => p.Current is not null && p.Current.Id != p.Old.Id).ToDictionary(p => p.Old.Id, p => p.Current!.Id);
        if (identities.Count > 0 && _diagnostics.Negotiated is { } negotiated)
            _diagnostics = _diagnostics with { Negotiated = negotiated.Select(item =>
            {
                if (!identities.TryGetValue(item.Text("receiver_id"), out var id)) return item;
                var fields = item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                fields["receiver_id"] = JsonSerializer.SerializeToElement(id);
                return JsonSerializer.SerializeToElement(fields);
            }).ToArray() };

        if (!previous.IsSoftwareGroup || !receiver.IsSoftwareGroup) return;
        foreach (var member in receiver.Members)
        {
            var old = previous.Members.FirstOrDefault(m => m.Address == member.Address && m.Port == member.Port);
            if (old is null || old.Id == member.Id) continue;
            if (_memberHosts.Remove(old.Id, out var host)) _memberHosts[member.Id] = host;
            if (_memberVolumes.Remove(old.Id, out var state)) _memberVolumes[member.Id] = state.Target is null ? state : state.Timeout();
        }
    }
    // Browsing describes a future connection. It cannot reconfigure a working transport.
    public async Task RefreshReceiverAsync(Receiver receiver, SessionSnapshot expected, IReadOnlyDictionary<string, string> migrations)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!OwnsDiscoverySnapshot(expected) || _receiver is not { } previous || receiver.IsSoftwareGroup != previous.IsSoftwareGroup
                    || receiver.IsGroup != previous.IsGroup || receiver.Id != migrations.GetValueOrDefault(previous.Id, previous.Id)) return;
                var settings = _settings.Clone();
                ReceiverCatalog.ApplyMigrations(settings, migrations, previous.Id);
                if (receiver.Online && receiver.Complete && (!receiver.IsSoftwareGroup || receiver.Members.Select(m => Receiver.PhysicalKey(m.DeviceId, m.Address)).Distinct().Count() == 2
                    && receiver.Members.Select(m => m.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2)) _discoveredReceiver = receiver;
                Receiver Identity(Receiver active, Receiver discovered) => active with { Id = discovered.Id, Name = discovered.Name, DeviceId = discovered.DeviceId,
                    Model = discovered.Model, GroupName = discovered.GroupName, Aliases = discovered.Aliases };
                var retained = Identity(previous, receiver);
                if (previous.IsGroup)
                    retained = retained with { Members = previous.Members.Select(member =>
                    {
                        var id = migrations.GetValueOrDefault(member.Id, member.Id);
                        var found = receiver.Members.FirstOrDefault(m => m.Id == id || m.Address == member.Address && m.Port == member.Port);
                        return found is null ? member : Identity(member, found);
                    }).ToArray() };
                RemapReceiverIdentities(previous, retained);
                var changes = ConnectionChanges(previous, _settings, receiver, settings);
                if (changes.Length > 0) log?.Invoke($"Discovery refresh retained transport; changed_fields={string.Join(",", changes)}");
                _receiver = retained; _settings = settings;
                PublishCurrent(_generation);
            }
        }
        finally { _serial.Release(); }
    }
    public async Task<bool> StopForDiscoveryConflictAsync(SessionSnapshot expected)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) { if (!OwnsDiscoverySnapshot(expected) || _receiver?.IsSoftwareGroup != true) return false; }
            log?.Invoke("Session stop reason=discovery_topology_conflict");
            await StopLockedAsync().ConfigureAwait(false); _receiver = null; Publish(_generation, PlaybackState.Idle);
            return true;
        }
        finally { _serial.Release(); }
    }
    public async Task SetMutedAsync(bool muted)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _muted = muted; await SendGainAsync().ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    private static object StandbyParameters(AppSettings settings, string receiverId) => new { enabled = settings.StandbyEnabled, silence_seconds = settings.ReadOptions(receiverId).StandbySeconds ?? settings.StandbySilenceSeconds };
    private async Task SendStandbyAsync()
    {
        await _equalizerWriter.WaitAsync().ConfigureAwait(false);
        try
        {
            IEngineConnection? connection; string? session; (bool Enabled, double Seconds) policy;
            lock (_sync)
            {
                if (!_equalizerReady || _receiver is null) return;
                connection = _connection; session = _sessionId;
                policy = (_settings.StandbyEnabled, _settings.ReadOptions(_receiver.Id).StandbySeconds ?? _settings.StandbySilenceSeconds);
                if (_sentStandby == policy) return;
            }
            if (connection is null || session is null || Snapshot.State == PlaybackState.Pairing) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.SendAsync(session, "set_standby", new { enabled = policy.Enabled, silence_seconds = policy.Seconds }, timeout.Token).ConfigureAwait(false);
            lock (_sync) { if (ReferenceEquals(_connection, connection) && _sessionId == session) _sentStandby = policy; }
        }
        finally { _equalizerWriter.Release(); }
    }
    private double Gain(Receiver receiver) => _muted ? 0 : _settings.Gain(_receiver?.Id ?? receiver.Id);
    private async Task SendGainAsync()
    {
        IEngineConnection? connection; string? session;
        lock (_sync) { connection = _connection; session = _sessionId; }
        if (connection is null || session is null || _receiver is null || Snapshot.State == PlaybackState.Pairing) return;
        try { await connection.SendAsync(session, "set_gain", new { gain = Gain(_receiver) }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { log?.Invoke(L.Format("Volume command deferred to the next session: {0}", error.Message)); }
    }
    private async Task RunStreamAsync(Receiver receiver, long epoch, CancellationToken cancellation)
    {
        var attempts = 0;
        while (!cancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (epoch != _generation || _receiver is null) return;
                if (attempts > 0) _receiver = RestartReceiver(_receiver, _settings);
                receiver = _receiver;
            }
            var startedAt = 0L;
            try
            {
                var settings = _settings;
                var peers = new List<object>();
                string? volumeHost = null;
                var hosts = new Dictionary<string, string>();
                foreach (var member in receiver.Peers.OrderByDescending(p => p.IsLeader))
                {
                    var host = await ResolveAsync(member.Address, cancellation).ConfigureAwait(false);
                    volumeHost ??= host;
                    hosts[member.Id] = host;
                    peers.Add(new { receiver_id = member.Id, host, port = member.Port, codecs = member.Codecs.Select(x => (int)x).ToArray(), profile = PeerProfile(receiver, member, settings) });
                }
                await using var connection = factory.Open();
                var session = Guid.NewGuid().ToString("N");
                SetConnection(connection, session);
                try
                {
                    lock (_sync) { if (epoch == _generation) { _diagnostics = _diagnostics with { Transport = null, Warnings = [], Negotiated = [], ClockStatus = null }; _warnings.Clear(); _deviceVolume = new(); _memberVolumes.Clear(); _memberHosts.Clear(); foreach (var pair in hosts) _memberHosts[pair.Key] = pair.Value; _volumeEdit?.Cancel(); _snapshot = _snapshot with { Metrics = null }; } }
                    Publish(epoch, PlaybackState.Connecting, attempts > 0 ? L.Format("Reconnecting ({0}/{1})", attempts, settings.MaxReconnectAttempts) : L.Get("Connecting…"));
                    var deadline = DateTime.UtcNow + _timing.Connect;
                    await connection.SendAsync(session, "hello", null, cancellation).ConfigureAwait(false);
                    var hello = await ReadEventAsync(connection, session, _timing.Connect, cancellation).ConfigureAwait(false);
                    EngineHandshake.RequireStreamingCapabilities(hello);
                    EngineHandshake.RequireCapabilities(hello, "standby_release");
                    if (settings.CaptureMode == "application") EngineHandshake.RequireCapabilities(hello, "application_capture");
                    if (receiver.IsSoftwareGroup) EngineHandshake.RequireCapabilities(hello, "member_volume");
                    var capabilities = await ReceiverCapabilityCatalog.QueryConnectionAsync(connection, session, hello, receiver, cancellation).ConfigureAwait(false);
                    var selected = capabilities.Select(id => ProfileOptions(receiver, receiver.Peers.Single(p => p.Id == id), settings), settings.SampleRate(receiver));
                    if (capabilities.Members.Count > 0 && selected.Count == 0) throw new InvalidOperationException(L.Get("The receiver does not support this stream profile. Choose Automatic or a supported option in Receivers."));
                    var sampleRate = selected.FirstOrDefault()?.Profile.SampleRate ?? (int.TryParse(settings.SampleRate(receiver), out var explicitRate) ? explicitRate : 44100);
                    await _equalizerWriter.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        await connection.SendAsync(session, "start", new { peers, group_mode = receiver.IsSoftwareGroup ? "software_stereo" : receiver.IsGroup ? "native_stereo" : "single", group_id = receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.StereoId : null, source = settings.CaptureMode == "application" ? "application" : "loopback", capture_application_path = settings.CaptureMode == "application" ? settings.CaptureApplicationPath : null, capture_process_id = settings.CaptureMode == "application" ? settings.CaptureApplicationInstance?.ProcessId : null, capture_process_started_at = settings.CaptureMode == "application" ? settings.CaptureApplicationInstance?.StartedAt : null, standby = StandbyParameters(settings, receiver.Id), duration_ms = 0, latency_ms = settings.Latency(receiver.Id), gain = Gain(receiver), timing = "ptp", capture_endpoint = settings.EffectiveEndpoint, sample_rate = sampleRate, equalizer = EffectiveEqualizer().WireParameters() }, cancellation).ConfigureAwait(false);
                        lock (_sync) { if (epoch == _generation) _snapshot = _snapshot with { StreamRate = sampleRate }; }
                        lock (_sync) { if (epoch == _generation && ReferenceEquals(_connection, connection)) _equalizerReady = true; }
                    }
                    finally { _equalizerWriter.Release(); }
                    await SendStandbyAsync().ConfigureAwait(false);
                    await SendEqualizerAsync(cancellation: cancellation).ConfigureAwait(false);
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var wait = startedAt == 0 ? deadline - DateTime.UtcNow : _timing.Read;
                        if (wait <= TimeSpan.Zero) throw new TimeoutException(L.Get("Timed out connecting to the audio device."));
                        var item = await ReadEventAsync(connection, session, wait, cancellation).ConfigureAwait(false);
                        var kind = item.Text("event");
                        if (kind == "error") throw new EngineFailure(EngineNotice.Parse(item));
                        if (kind == "stopped") throw new IOException(L.Get("The audio session ended unexpectedly."));
                        if (kind == "negotiated")
                        {
                            lock (_sync) { if (epoch != _generation) continue; _diagnostics = _diagnostics with { Negotiated = (_diagnostics.Negotiated ?? []).Where(n => item.Text("receiver_id").Length > 0 ? n.Text("receiver_id") != item.Text("receiver_id") : n.Text("host") != item.Text("host")).Append(item.Clone()).ToArray() }; }
                            // The handshake alone does not prove that the receiver can render
                            // the stream; this line records the codec, transport, timing and
                            // clock role needed to diagnose a silent session.
                            log?.Invoke($"Receiver negotiated: {item.Text("receiver_id", item.Text("host"))}; host {item.Text("host")}; codec {item.Text("codec")}; transport {item.Text("transport")}; timing {item.Text("timing")}; clock_role {item.Text("clock_role")}; {item.Integer("sample_rate")} Hz");
                        }
                        if (kind is "clock_status" or "scheduler_status")
                        {
                            lock (_sync) { if (epoch != _generation) continue; _diagnostics = kind == "clock_status" ? _diagnostics with { ClockStatus = item.Clone() } : _diagnostics with { SchedulerStatus = item.Clone() }; }
                        }
                        if (kind is "equalizer_changed" or "equalizer_error")
                        {
                            lock (_sync) { if (epoch != _generation || item.Integer("sequence") != _equalizerSequence) continue; }
                            EqualizerFeedback?.Invoke(kind == "equalizer_error" ? item.Text("message", L.Get("Could not update the equalizer.")) : null);
                        }
                        if (kind == "streaming")
                        {
                            startedAt = Stopwatch.GetTimestamp();
                            lock (_sync) { if (epoch != _generation) continue; _transportStreaming = true; }
                            await ApplyLocalMuteAsync().ConfigureAwait(false);
                            log?.Invoke($"Streaming started: {receiver.Id}; target latency {settings.Latency(receiver.Id)} ms; reconnect attempts {attempts}");
                            Publish(epoch, PlaybackState.Streaming);
                        }
                        else if (kind == "standby")
                        {
                            startedAt = Stopwatch.GetTimestamp();
                            await RestoreAudioAsync().ConfigureAwait(false);
                            var message = item.Text("reason") switch
                            {
                                "application_unavailable" => L.Get("Waiting for the selected application to start."),
                                "application_ambiguous" => L.Get("Multiple application instances are running. Select an instance in Settings → Audio capture."),
                                _ => L.Get("AirPlay session released. Waiting for audio.")
                            };
                            Publish(epoch, PlaybackState.Standby, message);
                        }
                        else if (kind == "resuming")
                        {
                            startedAt = 0; deadline = DateTime.UtcNow + _timing.Connect;
                            Publish(epoch, PlaybackState.Connecting, L.Get("Audio detected; reconnecting…"));
                        }
                        else if (kind == "device_volume" && startedAt != 0 && receiver.IsSoftwareGroup)
                        {
                            lock (_sync) { if (epoch != _generation) continue; var id = _memberHosts.FirstOrDefault(p => p.Value == item.Text("host")).Key; if (id is not null) _memberVolumes[id] = (_memberVolumes.GetValueOrDefault(id) ?? new()).Receive(item); }
                            PublishCurrent(epoch);
                        }
                        else if (kind == "device_volume" && startedAt != 0 && item.Text("host") == volumeHost)
                        {
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                var wasPending = _deviceVolume.Target is not null;
                                _deviceVolume = _deviceVolume.Receive(item);
                                if (wasPending && _deviceVolume.Target is null) _volumeEdit?.Cancel();
                            }
                            PublishCurrent(epoch);
                        }
                        else if (kind == "warning" && startedAt != 0)
                        {
                            var notice = EngineNotice.Parse(item);
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                var key = notice.Host + ":" + notice.Channel;
                                if (item.Boolean("recovered") == true) _warnings.Remove(key); else _warnings[key] = notice;
                                _diagnostics = _diagnostics with { Warnings = _warnings.Values.ToArray() };
                            }
                            log?.Invoke($"Engine warning: {item}");
                            var current = Snapshot; Publish(epoch, current.State, current.Message);
                        }
                        else if (kind == "transport_metrics" && startedAt != 0)
                        {
                            lock (_sync) { if (epoch != _generation) continue; _diagnostics = _diagnostics with { Transport = TransportMetrics.Parse(item) }; }
                            var current = Snapshot; Publish(epoch, current.State, current.Message);
                        }
                        else if (kind == "capture_metrics" && item.TryGetProperty("metrics", out var metrics))
                        {
                            var parsed = new StreamMetrics(metrics.Number("capture_to_send_p95_ms"), metrics.Number("max_queue_age_ms"), metrics.Integer("underrun_packets"), metrics.Integer("dropped_frames"), metrics.Integer("input_rate") is { } rate ? (int)rate : null);
                            var current = Snapshot;
                            Publish(epoch, current.State, current.Message, metrics: parsed);
                        }
                    }
                }
                finally { ClearConnection(connection); await RequestStopAsync(connection, session).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                await RestoreAudioAsync().ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) return;
                if (startedAt != 0 && Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(10)) attempts = 0;
                ++attempts;
                if (RecordFailure(epoch, error) is not { } notice) return;
                Publish(epoch, PlaybackState.Error, notice.Detail);
                if (!(_settings.ForceReconnect || receiver.IsSoftwareGroup || notice.Code is "timeline_expired" or "clock_lost") || attempts > _settings.MaxReconnectAttempts || notice.Retryable == false || notice.Retryable is null && IsAuthenticationFailure(error.Message)) return;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(_timing.Retry.TotalMilliseconds * Math.Pow(2, attempts - 1), 8000));
            log?.Invoke($"Session reconnect reason=transport_fault; code={_diagnostics.LastFault?.Code}; attempt={attempts}; delay_ms={delay.TotalMilliseconds}");
            Publish(epoch, PlaybackState.Connecting, L.Format("Disconnected; reconnecting ({0}/{1})", attempts, _settings.MaxReconnectAttempts));
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            lock (_sync) { if (epoch == _generation) _diagnostics = _diagnostics with { ReconnectCount = _diagnostics.ReconnectCount + 1 }; }
        }
    }
    private async Task PairMembersAsync(Receiver receiver, Func<Receiver, CancellationToken, Task<string?>> requestPin, long epoch, CancellationToken cancellation)
    {
        foreach (var member in receiver.Peers.OrderByDescending(p => p.IsLeader))
        {
            await using var connection = factory.Open();
            var session = Guid.NewGuid().ToString("N");
            SetConnection(connection, session);
            try
            {
                Publish(epoch, PlaybackState.Pairing, L.Format("Pairing {0} ({1})", member.Name, member.Address));
                await connection.SendAsync(session, "hello", null, cancellation).ConfigureAwait(false);
                EngineHandshake.RequireCapabilities(await ReadEventAsync(connection, session, _timing.Connect, cancellation).ConfigureAwait(false), member.RequiresPassword ? ["receiver_password"] : []);
                await connection.SendAsync(session, "pair", new { password_required = member.RequiresPassword, peer = new { host = await ResolveAsync(member.Address, cancellation).ConfigureAwait(false), port = member.Port } }, cancellation).ConfigureAwait(false);
                while (true)
                {
                    var item = await ReadEventAsync(connection, session, _timing.Connect, cancellation).ConfigureAwait(false);
                    switch (item.Text("event"))
                    {
                        case "pin_required":
                        case "password_required":
                            using (var pinTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                            {
                                pinTimeout.CancelAfter(_timing.Pin);
                                var pin = await requestPin(member with { RequiresPassword = item.Text("event") == "password_required" }, pinTimeout.Token).ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(pin)) { Publish(epoch, PlaybackState.Idle, L.Get("Pairing cancelled.")); throw new PairCancelledException(); }
                                await connection.SendAsync(session, "pair_pin", new { pin }, cancellation).ConfigureAwait(false);
                            }
                            break;
                        case "paired": goto Paired;
                        case "error": throw new EngineFailure(EngineNotice.Parse(item));
                    }
                }
            Paired:;
            }
            finally { ClearConnection(connection); await RequestStopAsync(connection, session).ConfigureAwait(false); }
        }
    }
    private EngineNotice? RecordFailure(long epoch, Exception error)
    {
        var notice = error is EngineFailure engineFailure ? engineFailure.Notice : new EngineNotice("host_error", "", "desktop", error.Message);
        PlaybackDiagnostics diagnostics;
        lock (_sync)
        {
            if (epoch != _generation) return null;
            // A failed reconnect must not erase the last streaming failure snapshot.
            diagnostics = _diagnostics = _diagnostics with { LastFault = notice, CaptureBeforeFault = _snapshot.Metrics ?? _diagnostics.CaptureBeforeFault, TransportBeforeFault = _diagnostics.Transport ?? _diagnostics.TransportBeforeFault };
        }
        log?.Invoke($"Session failure: {JsonSerializer.Serialize(diagnostics)}\n{error}");
        return notice;
    }
    private sealed class PairCancelledException : OperationCanceledException;
    private static bool IsAuthenticationFailure(string message) => new[] { "pairing", "srp", "verification", "credentials", "signature", "identity", "authentication" }.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));
    private void SetConnection(IEngineConnection connection, string session) { lock (_sync) { _connection = connection; _sessionId = session; _equalizerReady = false; _sentStandby = null; } }
    private void ClearConnection(IEngineConnection connection) { lock (_sync) if (ReferenceEquals(_connection, connection)) { _connection = null; _sessionId = null; _equalizerReady = false; _sentStandby = null; } }
    private static async Task RequestStopAsync(IEngineConnection connection, string session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await connection.SendAsync(session, "stop", null, timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
    private static async Task<JsonElement> ReadEventAsync(IEngineConnection connection, string session, TimeSpan wait, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var item = await EngineEvents.ReadAsync(connection, timeout.Token).ConfigureAwait(false) ?? throw new IOException(L.Get("The audio engine exited."));
                if (EngineEvents.IsProcessRejection(item)) return item;
                if (item.Number("version") == 1 && item.Text("session_id") == session) return item;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException(L.Get("The audio engine response timed out.")); }
    }
    private static async Task<string> ResolveAsync(string host, CancellationToken cancellation)
    {
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork) return host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? throw new IOException(L.Format("Cannot resolve IPv4 address: {0}", host));
    }
    private async Task RestoreAudioAsync()
    {
        lock (_sync) _transportStreaming = false;
        await ApplyLocalMuteAsync().ConfigureAwait(false);
    }
    private async Task ApplyLocalMuteAsync()
    {
        await _audioWriter.WaitAsync().ConfigureAwait(false);
        try
        {
            bool mute; string? endpoint;
            lock (_sync) { mute = _transportStreaming && _settings.ShouldMuteEndpoint; endpoint = _settings.EffectiveEndpoint; }
            if (mute) await audio.MuteAsync(endpoint).ConfigureAwait(false);
            else await RestoreAudioCoreAsync().ConfigureAwait(false);
        }
        finally { _audioWriter.Release(); }
    }
    private async Task RestoreAudioCoreAsync()
    {
        try { await audio.RestoreAsync().ConfigureAwait(false); } catch (Exception error) { log?.Invoke(L.Format("Failed to restore local mute state: {0}", error.Message)); }
    }
    private void Publish(long epoch, PlaybackState state, string message = "", StreamMetrics? metrics = null)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (epoch != _generation) return;
            snapshot = new(state, _receiver, message, metrics ?? _snapshot.Metrics, _receiver is null ? 0 : _settings.Latency(_receiver.Id), _diagnostics, _receiver is null ? 0 : _snapshot.StreamRate, _deviceVolume, new Dictionary<string, DeviceVolumeState>(_memberVolumes)) { Generation = epoch };
            _snapshot = snapshot;
        }
        Changed?.Invoke(snapshot);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _equalizerDisposed = true; _equalizerEdit?.Cancel(); _equalizerEdit = null; _equalizerEditTask = null; }
        await StopAsync().ConfigureAwait(false);
        _serial.Dispose();
        _audioWriter.Dispose();
    }
}
