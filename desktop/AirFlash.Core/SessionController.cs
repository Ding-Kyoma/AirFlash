using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
namespace AirFlash.Core;

public enum PlaybackState { Idle, Connecting, Streaming, Standby, Pairing, Error, Preempted }
public enum SessionStartIntent { Manual, Automatic }
public sealed record StreamMetrics(double? CaptureToSendP95 = null, double? MaxQueueAge = null, long? Underruns = null, long? DroppedFrames = null, int? InputRate = null,
    long? Rebinds = null, long? SilentProbes = null, long? RebindStorms = null)
{
    public ulong? PaddedFrames { get; init; }
    public ulong? FullyPaddedPackets { get; init; }
}
public sealed record SessionSnapshot(PlaybackState State, Receiver? Receiver = null, string Message = "", StreamMetrics? Metrics = null, int TargetLatency = 0, PlaybackDiagnostics? Diagnostics = null, int StreamRate = 0, DeviceVolumeState? DeviceVolume = null, IReadOnlyDictionary<string, DeviceVolumeState>? MemberVolumes = null)
{
    public bool IsActive => State is PlaybackState.Connecting or PlaybackState.Streaming or PlaybackState.Standby or PlaybackState.Pairing;
    internal long Generation { get; init; }
    public LatencyRuntime? Latency { get; init; }
    public int? PendingLatency { get; init; }
    public string ActionHint { get; init; } = "";
    public bool IsRetrying { get; init; }
}
public sealed record SessionTiming(TimeSpan Connect, TimeSpan Read, TimeSpan Pin, TimeSpan Retry)
{
    public static SessionTiming Default { get; } = new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1));
}
public sealed record SessionCapture(SessionSnapshot Snapshot, long Owner, long AutomaticIntent);
public sealed record DiagnosticCapture(JsonElement? Summary, IReadOnlyList<PlaybackCounterDelta>? CounterHistory);
internal readonly record struct StreamingAttempt(long Owner, long Intent, long Transport);

public sealed class SessionController(IEngineFactory factory, IAudioService audio, Action<string>? log = null, SessionTiming? timing = null, Action<DesktopSessionRecord>? sessionRecords = null, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private long _soundGlowSequence;
    private long _soundGlowTransport;
    private bool _soundGlowSupported;
    private readonly SemaphoreSlim _soundGlowWriter = new(1, 1);
    public bool SoundGlowSupported { get { lock (_sync) return _soundGlowSupported; } }
    public event Action<JsonElement>? SoundGlowFeedback;
    internal async Task<bool> TryRestartForSoundGlowAsync(CancellationToken cancellation = default)
    {
        using var operation = TryAdmitPassiveOperation();
        if (operation is null || cancellation.IsCancellationRequested) return false;
        StreamingAttempt attempt;
        lock (_sync)
        {
            if (!_soundGlowSupported || CaptureStreamingAttempt() is not { } current) return false;
            attempt = current.Attempt with { Intent = ++_automaticIntent };
        }
        try { await _serial.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        try
        {
            Receiver receiver;
            AppSettings settings;
            lock (_sync)
            {
                if (CaptureStreamingAttempt() is not { } current || current.Attempt != attempt) return false;
                receiver = current.Receiver;
                settings = _settings;
            }
            return await StartLockedAsync(receiver, settings, null, "sound_glow_reset", intent: SessionStartIntent.Manual,
                expectedIntent: attempt.Intent, canAdmit: () => !cancellation.IsCancellationRequested).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    public async Task SendSoundGlowTestAsync(string palette, CancellationToken cancellation = default)
    {
        if (palette is not ("metadata_only" or "red" or "blue" or "green"))
            throw new ArgumentException("Unknown test palette.", nameof(palette));
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) throw new InvalidOperationException(L.Get("Start streaming before testing Sound Glow."));
        IEngineConnection connection;
        string session;
        CancellationToken lifetime;
        StreamingAttempt attempt;
        lock (_sync)
        {
            if (!_transportStreaming || _snapshot.State != PlaybackState.Streaming || _connection is null || _sessionId is null || _lifetime is null)
                throw new InvalidOperationException(L.Get("Start streaming before testing Sound Glow."));
            if (!_soundGlowSupported)
                throw new InvalidOperationException(L.Get("This audio engine does not support the Sound Glow experiment."));
            connection = _connection;
            session = _sessionId;
            lifetime = _lifetime.Token;
            attempt = new(_generation, _automaticIntent, _remoteTransport);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await _soundGlowWriter.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            long sequence;
            lock (_sync)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_passiveDisposed || !_transportStreaming || _snapshot.State != PlaybackState.Streaming
                    || attempt != new StreamingAttempt(_generation, _automaticIntent, _remoteTransport)
                    || !ReferenceEquals(connection, _connection))
                    throw new InvalidOperationException(L.Get("Start streaming before testing Sound Glow."));
                sequence = ++_soundGlowSequence;
                _soundGlowTransport = attempt.Transport;
            }
            await connection.SendAsync(session, "set_sound_glow", new { sequence, palette }, timeout.Token).ConfigureAwait(false);
            log?.Invoke($"Sound Glow test requested: sequence={sequence}; palette={palette}");
        }
        finally { _soundGlowWriter.Release(); }
    }
    private readonly SemaphoreSlim _audioWriter = new(1, 1);
    private readonly SemaphoreSlim _diagnosticWriter = new(1, 1);
    private readonly SemaphoreSlim _nowPlayingWriter = new(1, 1);
    private PendingDiagnostic? _diagnosticReply;
    private DiagnosticCapture? _lastDiagnostic;
    private long? _nativeSessionEpoch;
    /// <summary>The live Now Playing source's latest value, sent again when a stream
    /// starts. It follows the source, not a session: <see cref="ClearNowPlayingAsync"/>
    /// drops it when the source goes away. Stopping a session keeps it because the
    /// pump only republishes changes, so a paused player would otherwise show
    /// nothing in the next session.</summary>
    private NowPlaying? _nowPlaying;
    private long _nowPlayingSampledAt;
    private bool _nowPlayingMayBeSet;
    // What the current engine's hello advertised; a connection that never said
    // hello (or a pairing connection) gets neither command.
    private bool _nowPlayingSupported, _diagnosticsSupported;
    private readonly object _sync = new();
    private readonly SessionTiming _timing = timing ?? SessionTiming.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly AdaptiveLatency _adaptive = new(clock ?? TimeProvider.System);
    // Q40: the companion record for the engine's session-summary.csv. Tests pass a
    // collector; production appends beside the engine file.
    private readonly Action<DesktopSessionRecord> _sessionRecords = sessionRecords ?? DesktopSessionLog.Append;
    private CancellationTokenSource? _lifetime;
    private Task? _work;
    private EngineShutdownException? _cleanupFailure;
    public bool NeedsCleanup => Volatile.Read(ref _cleanupFailure) is not null;
    private IEngineConnection? _connection;
    private string? _sessionId;
    private AppSettings _settings = new();
    private Receiver? _receiver;
    private Receiver? _discoveredReceiver;
    private long _generation;
    private long _automaticIntent;
    private bool _passiveDisposed;
    private int _passiveOperations;
    private TaskCompletionSource? _passiveDrained;
    private Task? _disposeTask;
    private bool _muted;
    private bool _transportStreaming;
    private long _remoteTransport;
    private bool _manualRecoveryRequired;
    public bool RequiresManualReconnect { get { lock (_sync) return _manualRecoveryRequired; } }
    private readonly SemaphoreSlim _equalizerWriter = new(1, 1);
    private EqualizerSettings? _equalizerPreview;
    private Guid? _equalizerOwner;
    private long _equalizerSequence;
    private CancellationTokenSource? _equalizerEdit;
    private Task? _equalizerEditTask;
    private bool _startSent, _equalizerDisposed;
    private (bool Enabled, double Seconds)? _sentStandby;
    public event Action<string?>? EqualizerFeedback;
    /// <summary>Raised when a receiver sends a transport command back (its own
    /// buttons, or a remote pointed at it). The engine has already acknowledged it;
    /// acting on it is the desktop's job.</summary>
    public event Action<RemoteCommand>? RemoteCommandReceived;
    internal event Action<RemoteCommand, RemoteCommandAuthority>? OwnedRemoteCommandReceived;
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
        using var operation = TryAdmitPassiveOperation();
        var token = edit.Token;
        try
        {
            if (operation is null) return;
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
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) return;
        await _equalizerWriter.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            IEngineConnection? connection; string? session; EqualizerSettings equalizer; long sequence, epoch;
            lock (_sync)
            {
                if (_equalizerDisposed || !_startSent || expectedSequence is { } expected && expected != _equalizerSequence) return;
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
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) return;
        if (Snapshot.Receiver?.IsSoftwareGroup == true) { await SetMemberVolumeAsync(receiverId, value).ConfigureAwait(false); return; }
        CancellationTokenSource edit;
        CancellationToken token;
        long sequence, epoch;
        lock (_sync)
        {
            if (_passiveDisposed || _lifetime is null || _receiver?.Id != receiverId || !_deviceVolume.Available
                || _snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby)) return;
            _volumeEdit?.Cancel();
            _volumeEdit = edit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = edit.Token; sequence = ++_volumeSequence; epoch = _generation;
            _deviceVolume = _deviceVolume.Begin(Math.Clamp(value, 0, 100), sequence);
        }
        try
        {
            PublishCurrent(epoch);
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
        finally
        {
            lock (_sync) { if (ReferenceEquals(_volumeEdit, edit)) _volumeEdit = null; }
            edit.Dispose();
        }
    }
    private void PublishCurrent(long epoch)
    {
        // Hold the state lock through publication so an old state cannot replace stop/reconnect.
        lock (_sync) { if (epoch == _generation) Publish(epoch, _snapshot.State, _snapshot.Message, isRetrying: _snapshot.IsRetrying); }
    }
    /// <summary>
    /// Device volume reads are accepted while an attempt is preparing or active,
    /// so a restored initial value racing the streaming event is not dropped.
    /// Writes stay limited to Streaming/Standby.
    /// </summary>
    private bool AcceptsVolumeReads() => Snapshot.State is PlaybackState.Connecting or PlaybackState.Streaming or PlaybackState.Standby;
    private PlaybackDiagnostics _diagnostics = new();
    private int _healthLogPending;

    private void QueueHealthLog(string sample, bool advisory)
    {
        if (log is not { } write || Interlocked.CompareExchange(ref _healthLogPending, 1, 0) != 0) return;
        // Keep at most one write in flight so a slow disk cannot stall the
        // engine reader or build up a queue across playback attempts.
        _ = Task.Run(() =>
        {
            try
            {
                write($"Session health: {sample}");
                if (advisory) write($"Fixed latency looks tight: {sample}");
            }
            catch (Exception) { /* Health logging is best effort. */ }
            finally { Volatile.Write(ref _healthLogPending, 0); }
        });
    }

    private async Task SetMemberVolumeAsync(string memberId, int value)
    {
        long epoch, sequence; string host, session; IEngineConnection connection; CancellationToken token;
        lock (_sync)
        {
            if (_passiveDisposed || _lifetime is null || _receiver?.IsSoftwareGroup != true || _snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby) || !_memberVolumes.TryGetValue(memberId, out var state) || !state.Available || !_memberHosts.TryGetValue(memberId, out host!) || _connection is null || _sessionId is null) return;
            epoch = _generation; sequence = ++_volumeSequence; connection = _connection; session = _sessionId; token = _lifetime.Token;
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
    public SessionCapture Capture() { lock (_sync) return new(_snapshot, _generation, _automaticIntent); }
    internal (StreamingAttempt Attempt, Receiver Receiver)? CaptureStreamingAttempt()
    {
        lock (_sync)
        {
            if (!_transportStreaming || _snapshot.State != PlaybackState.Streaming || _snapshot.IsRetrying
                || _receiver is not { } receiver || _lifetime?.IsCancellationRequested != false || _passiveDisposed) return null;
            return (new(_generation, _automaticIntent, _remoteTransport), receiver);
        }
    }
    public void InvalidateAutomaticIntent() => BeginExplicitIntent();
    public long BeginExplicitIntent() { lock (_sync) return ++_automaticIntent; }
    public bool IsIntentCurrent(long intent) { lock (_sync) return !_passiveDisposed && intent == _automaticIntent; }
    public bool UsesDeviceLatency(string globalMode)
    {
        lock (_sync) return _receiver is { } receiver && _settings.UsesDeviceLatency(receiver, globalMode);
    }
    public event Action<SessionSnapshot>? Changed;
    public bool Muted => _muted;
    private static ReceiverOptions ProfileOptions(Receiver receiver, Receiver member, AppSettings settings) => settings.ReadOptions(receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.Id : member.Id);
    private static object PeerProfile(Receiver receiver, Receiver member, AppSettings settings, ReceiverProfile? negotiated = null, int? adaptiveTarget = null)
    {
        var options = ProfileOptions(receiver, member, settings);
        return new { codec = options.Codec, transport = options.Transport, timing = options.Timing == "follow" ? "ptp" : options.Timing,
            follow_receiver_clock = options.Timing == "follow" || options.Timing == "auto" && member.FollowReceiverClock, features = member.Features,
            channel = receiver.IsSoftwareGroup ? member.Channel : "stereo", sync_offset_ms = receiver.IsSoftwareGroup ? member.SyncOffsetMs : 0,
            latency_ms = adaptiveTarget ?? InitialLatency(receiver, member, settings, negotiated) };
    }
    /// <summary>
    /// The last audible volume to restore for this peer. Single receivers and
    /// native stereo groups share one group-level value; software group members
    /// remember their own hardware volume.
    /// </summary>
    private static int? RememberedVolume(Receiver receiver, Receiver member, AppSettings settings)
    {
        var options = settings.ReadOptions(receiver.IsSoftwareGroup ? member.Id : receiver.Id);
        return options.LastVolume is > 0 and var volume ? volume : null;
    }
    private static int InitialLatency(Receiver receiver, Receiver member, AppSettings settings, ReceiverProfile? negotiated)
    {
        var target = receiver.IsSoftwareGroup ? member : receiver;
        if ((settings.ReadOptions(target.Id).LatencyMode ?? settings.LatencyMode) != LatencyPolicy.Mode)
            return settings.Latency(target);
        if (negotiated is null)
        {
            var options = ProfileOptions(receiver, member, settings);
            return LatencyPolicy.For(receiver, options.Codec == "auto" ? null : options.Codec, options.Timing is not ("ntp")).Initial;
        }
        return LatencyPolicy.For(receiver, negotiated.SelectedCodec, negotiated.SelectedTiming is "ptp" or "follow").Initial;
    }
    private static Dictionary<string, string?> ConnectionFields(Receiver receiver, AppSettings settings)
    {
        var application = settings.CaptureMode == "application";
        return new()
        {
            ["receiver"] = JsonSerializer.Serialize(new { mode = receiver.PlaybackGroupMode,
                group = receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.StereoId : null,
                peers = receiver.Peers.OrderBy(m => m.Address, StringComparer.Ordinal).ThenBy(m => m.Port).Select(m => new { m.Address, m.Port, leader = receiver.IsGroup && m.IsLeader }) }),
            ["capture"] = JsonSerializer.Serialize(new { settings.CaptureMode, endpoint = settings.EffectiveEndpoint,
                path = application ? settings.CaptureApplicationPath : null, process = application ? settings.CaptureApplicationInstance?.ProcessId : null,
                started = application ? settings.CaptureApplicationInstance?.StartedAt : null }),
            ["latency"] = settings.Latency(receiver).ToString(),
            ["force_latency"] = JsonSerializer.Serialize(receiver.Peers.OrderBy(m => m.Address, StringComparer.Ordinal).ThenBy(m => m.Port).Select(m => settings.ForcesLatency(receiver.IsSoftwareGroup ? m.Id : receiver.Id))),
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
    public async Task StartAsync(Receiver receiver, AppSettings settings, SessionStartIntent intent = SessionStartIntent.Manual)
    {
        if (intent == SessionStartIntent.Manual)
            await TryStartExplicitAsync(receiver, settings, BeginExplicitIntent()).ConfigureAwait(false);
        else
        {
            var expected = Capture();
            await TryStartAutomaticAsync(receiver, settings, expected.Owner, expected.AutomaticIntent).ConfigureAwait(false);
        }
    }
    public async Task PairAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>> requestPin)
    {
        await TryPairExplicitAsync(receiver, settings, requestPin, BeginExplicitIntent()).ConfigureAwait(false);
    }
    public Task<bool> TryStartExplicitAsync(Receiver receiver, AppSettings settings, long expectedIntent)
        => StartExplicitAsync(receiver, settings, null, expectedIntent);
    public Task<bool> TryPairExplicitAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>> requestPin, long expectedIntent)
        => StartExplicitAsync(receiver, settings, requestPin, expectedIntent);
    private static void ValidateStart(Receiver receiver, AppSettings settings, bool pairing = false)
    {
        if (pairing && !receiver.Complete)
            throw new InvalidOperationException(L.Get(receiver.IsMultiRoom
                ? "Wait until all multi-room members are online before pairing."
                : "Wait until both stereo pair members are online before pairing."));
        if (!receiver.Online) throw new InvalidOperationException(L.Get("The receiver is unavailable."));
        if (!receiver.Complete) throw new InvalidOperationException(L.Get(receiver.IsMultiRoom
            ? "All multi-room members must be online." : "Both stereo pair members must be online."));
        if (settings.Validate() is { } validation) throw new ArgumentException(validation, nameof(settings));
        if (LatencyPolicy.ForceConflict(settings, receiver) is { } conflict) throw new InvalidOperationException(conflict);
        if (receiver.IsSoftwareGroup && !receiver.HasDistinctSoftwareMembers)
            throw new InvalidOperationException(L.Get(receiver.IsMultiRoom
                ? "Multi-room groups require distinct independent receivers." : "Stereo groups require two distinct independent receivers."));
    }
    private async Task<bool> StartExplicitAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>>? requestPin, long expectedIntent)
    {
        if (!IsIntentCurrent(expectedIntent)) return false;
        ValidateStart(receiver, settings, pairing: requestPin is not null);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsIntentCurrent(expectedIntent)) return false;
            return await StartLockedAsync(receiver, settings, requestPin, requestPin is null ? "user_play" : "user_pair", intent: SessionStartIntent.Manual, expectedIntent: expectedIntent).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public async Task<bool> TryStartAutomaticAsync(Receiver receiver, AppSettings settings, long expectedOwner, long expectedIntent, Func<bool>? canAdmit = null)
    {
        if (!receiver.Online || !receiver.Complete) return false;
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
                if (_passiveDisposed || _generation != expectedOwner || _automaticIntent != expectedIntent || _snapshot.IsActive || _manualRecoveryRequired) return false;
            ValidateStart(receiver, settings);
            return await StartLockedAsync(receiver, settings, null, "automatic_play", expectedIntent: expectedIntent, canAdmit: canAdmit).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public async Task<bool> TryRestartForEndpointAsync(AppSettings settings, long expectedOwner, long expectedIntent, SessionSettingsContext? context = null)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            Receiver receiver;
            lock (_sync)
            {
                if (_passiveDisposed || _generation != expectedOwner || _automaticIntent != expectedIntent || !_snapshot.IsActive
                    || _snapshot.State == PlaybackState.Pairing || _receiver is null || _settings.CaptureMode != "loopback" || settings.CaptureMode != "loopback") return false;
                settings = context?.Project(settings, _settings, _receiver) ?? settings;
                receiver = RestartReceiver(_receiver, settings);
            }
            return await StartLockedAsync(receiver, settings, null, "default_endpoint_change", expectedIntent: expectedIntent).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private sealed record EndpointRecovery(Task Worker, long Owner, long Intent);
    private EndpointRecovery? _pendingEndpointRecovery, _activeEndpointRecovery;
    private Task? _endpointRecoveryTask;
    public event Action<Exception>? EndpointRecoveryFailed;
    private async Task RestoreInactiveAudioAsync()
    {
        await _audioWriter.WaitAsync().ConfigureAwait(false);
        try { await audio.RestoreAsync().ConfigureAwait(false); }
        finally { _audioWriter.Release(); }
    }
    public async Task<bool> TryRestoreAudioIfInactiveAsync(long expectedOwner, long expectedIntent)
    {
        lock (_sync) { if (_passiveDisposed) return false; }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_passiveDisposed || _generation != expectedOwner || _automaticIntent != expectedIntent || _snapshot.IsActive) return false;
                if (_work is { IsCompleted: false } worker)
                {
                    // The last worker restore may have failed just before its task settles. Keep one
                    // endpoint-return notification without competing with that worker or a retry.
                    if (!MatchesRecovery(_activeEndpointRecovery, worker, expectedOwner, expectedIntent)
                        && !MatchesRecovery(_pendingEndpointRecovery, worker, expectedOwner, expectedIntent))
                        _pendingEndpointRecovery = new(worker, expectedOwner, expectedIntent);
                    _endpointRecoveryTask ??= Task.Run(RecoverEndpointAfterWorkerAsync);
                    return false;
                }
            }
            // A new owner cannot commit while an inactive endpoint-recovery restore is awaited.
            await RestoreInactiveAudioAsync().ConfigureAwait(false);
            return true;
        }
        finally { _serial.Release(); }
    }
    private static bool MatchesRecovery(EndpointRecovery? recovery, Task worker, long owner, long intent)
        => recovery is not null && ReferenceEquals(recovery.Worker, worker) && recovery.Owner == owner && recovery.Intent == intent;
    private async Task RecoverEndpointAfterWorkerAsync()
    {
        while (true)
        {
            EndpointRecovery recovery;
            lock (_sync)
            {
                if (_passiveDisposed || _pendingEndpointRecovery is not { } pending)
                {
                    _activeEndpointRecovery = null; _endpointRecoveryTask = null; return;
                }
                recovery = pending; _pendingEndpointRecovery = null; _activeEndpointRecovery = recovery;
            }
            // Never await an unsettled worker under _serial: its retry can run until Stop cancels it.
            try { await recovery.Worker.ConfigureAwait(false); }
            catch (Exception error) { log?.Invoke(error.ToString()); }
            Exception? failure = null;
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sync)
                    if (_passiveDisposed || _generation != recovery.Owner || _automaticIntent != recovery.Intent
                        || _snapshot.IsActive || _work is { IsCompleted: false }) continue;
                await RestoreInactiveAudioAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                _serial.Release();
                lock (_sync) _activeEndpointRecovery = null;
            }
            if (failure is not null) EndpointRecoveryFailed?.Invoke(failure);
        }
    }
    private async Task<bool> StartLockedAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>>? requestPin, string reason, string[]? changedFields = null, SessionStartIntent intent = SessionStartIntent.Automatic, long? expectedIntent = null, Func<bool>? canAdmit = null)
    {
        lock (_sync)
        {
            if (_passiveDisposed || NeedsCleanup || _manualRecoveryRequired && intent == SessionStartIntent.Automatic
                || expectedIntent is { } expected && expected != _automaticIntent || canAdmit?.Invoke() == false) return false;
        }
        EnsureMuteReady(settings);
        log?.Invoke($"Session start reason={reason}; changed_fields={string.Join(",", changedFields ?? [])}");
        await StopLockedAsync().ConfigureAwait(false);
        long epoch;
        CancellationToken cancellation;
        lock (_sync)
        {
            if (_passiveDisposed || _manualRecoveryRequired && intent == SessionStartIntent.Automatic
                || expectedIntent is { } expected && expected != _automaticIntent || canAdmit?.Invoke() == false)
            {
                _receiver = null;
                Publish(_generation, PlaybackState.Idle);
                return false;
            }
            if (!MuteRecoveryAvailable(settings))
            {
                _receiver = null;
                Publish(_generation, PlaybackState.Idle);
                EnsureMuteReady(settings);
            }
            if (intent == SessionStartIntent.Manual) _manualRecoveryRequired = false;
            _settings = settings.Clone(); _receiver = receiver;
            epoch = ++_generation;
            _diagnostics = new(); _warnings.Clear(); _snapshot = new(PlaybackState.Idle); _lastDiagnostic = null; _adaptive.Reset();
            _lifetime = new(); cancellation = _lifetime.Token;
            Publish(epoch, requestPin is null ? PlaybackState.Connecting : PlaybackState.Pairing);
        }
        _work = Task.Run(async () =>
        {
            try
            {
                if (requestPin is not null) await PairMembersAsync(receiver, requestPin, epoch, cancellation).ConfigureAwait(false);
                await RunStreamAsync(receiver, epoch, intent, cancellation).ConfigureAwait(false);
            }
            catch (PairCancelledException) { }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (EngineShutdownException error)
            {
                Volatile.Write(ref _cleanupFailure, error);
                if (RecordFailure(epoch, error) is { } notice) Publish(epoch, PlaybackState.Error, FailureMessage(receiver, notice), failure: notice);
                throw;
            }
            catch (Exception error) { if (RecordFailure(epoch, error) is { } notice) Publish(epoch, FailureState(notice), FailureMessage(receiver, notice), failure: notice); }
            finally { await RestoreAudioAsync().ConfigureAwait(false); }
        }, CancellationToken.None);
        return true;
    }
    public async Task StopAsync()
    {
        InvalidateAutomaticIntent();
        // A live command may hold _serial while waiting for the engine pipe.
        // Cancel the worker first so its disposal can interrupt that write.
        CancellationTokenSource? lifetime;
        lock (_sync) lifetime = _lifetime;
        if (lifetime is not null)
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        await _serial.WaitAsync().ConfigureAwait(false);
        try { log?.Invoke("Session stop reason=user_stop"); await StopLockedAsync(retryCleanup: true).ConfigureAwait(false); _receiver = null; Publish(_generation, PlaybackState.Idle); }
        finally { _serial.Release(); }
    }
    private async Task StopLockedAsync(bool retryCleanup = false)
    {
        if (!retryCleanup && Volatile.Read(ref _cleanupFailure) is { } pending) throw pending;
        lock (_sync) { ++_generation; _discoveredReceiver = null; _volumeEdit?.Cancel(); _volumeEdit = null; _deviceVolume = new(); _memberVolumes.Clear(); _memberHosts.Clear(); }
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);
        var cleanup = retryCleanup ? Volatile.Read(ref _cleanupFailure) : null;
        if (_work is not null)
        {
            // The failure is published before the worker finishes restoring local audio.
            // Join it before retrying process disposal, and surface a newly observed failure.
            try { await _work.ConfigureAwait(false); }
            catch (EngineShutdownException error) when (ReferenceEquals(error, cleanup)) { }
            _work = null;
        }
        if (cleanup is not null)
        {
            await cleanup.Connection.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _cleanupFailure, null);
        }
        _lifetime?.Dispose(); _lifetime = null; _work = null;
        await RestoreAudioAsync().ConfigureAwait(false);
    }
    public async Task UpdateSettingsAsync(AppSettings settings)
        => await TryUpdateSettingsAsync(settings, Capture()).ConfigureAwait(false);
    public async Task<bool> TryUpdateSettingsAsync(AppSettings settings, SessionCapture expected, SessionSettingsContext? context = null)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_passiveDisposed || expected.Owner != _generation || expected.AutomaticIntent != _automaticIntent) return false;
                settings = context?.Project(settings, _settings, _receiver) ?? settings;
            }
            var old = _settings;
            if (_receiver is { IsSoftwareGroup: true } group && settings.SoftwareGroups.All(g => g.Id != group.Id))
            {
                await StopLockedAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    _receiver = null;
                    if (!IsIntentCurrent(expected.AutomaticIntent)) { Publish(_generation, PlaybackState.Idle); return false; }
                    _settings = settings.Clone(); Publish(_generation, PlaybackState.Idle);
                }
                return true;
            }
            var previous = _receiver;
            var next = previous;
            if (previous is { IsSoftwareGroup: true } software)
            {
                var catalog = software.Members.Concat(settings.ManualReceivers.Select(m => new Receiver(m.Id, m.Name, m.Host, m.Port) { IsManual = true }));
                next = SoftwareStereo.Resolve(catalog, settings).First(g => g.Id == software.Id);
                if (!next.Complete || !next.Online)
                {
                    await StopLockedAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        _receiver = null;
                        if (!IsIntentCurrent(expected.AutomaticIntent)) { Publish(_generation, PlaybackState.Idle); return false; }
                        _settings = settings.Clone(); Publish(_generation, PlaybackState.Error, L.Get("Stereo groups require two distinct independent receivers."));
                    }
                    return true;
                }
            }
            if (next is { } receiver && Snapshot.IsActive && Snapshot.State != PlaybackState.Pairing && ConnectionChanges(previous ?? receiver, old, receiver, settings) is { Length: > 0 } changes)
                return await StartLockedAsync(RestartReceiver(receiver, settings), settings, null, "settings_change", changes, expectedIntent: expected.AutomaticIntent).ConfigureAwait(false);
            lock (_sync) { _receiver = next; _settings = settings.Clone(); ++_equalizerSequence; }
            await ApplyLocalMuteAsync().ConfigureAwait(false);
            await SendGainAsync().ConfigureAwait(false);
            await SendStandbyAsync().ConfigureAwait(false);
            await SendEqualizerAsync().ConfigureAwait(false);
            return true;
        }
        finally { _serial.Release(); }
    }
    public async Task UpdateReceiverAsync(Receiver receiver, AppSettings settings)
        => await TryUpdateReceiverAsync(receiver, settings, Capture()).ConfigureAwait(false);
    public async Task<bool> TryUpdateReceiverAsync(Receiver receiver, AppSettings settings, SessionCapture expected)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
                if (_passiveDisposed || expected.Owner != _generation || expected.AutomaticIntent != _automaticIntent) return false;
            if (_receiver is not { } previous) return false;
            if (!receiver.Online || !receiver.Complete) return false;
            ValidateStart(receiver, settings);
            if (Snapshot.IsActive && Snapshot.State != PlaybackState.Pairing && ConnectionChanges(previous, _settings, receiver, settings) is { Length: > 0 } changes)
                return await StartLockedAsync(receiver, settings, null, "settings_change", changes, expectedIntent: expected.AutomaticIntent).ConfigureAwait(false);
            else
            {
                lock (_sync)
                {
                    RemapReceiverIdentities(previous, receiver);
                    _receiver = receiver; _settings = settings.Clone();
                }
                await SendStandbyAsync().ConfigureAwait(false);
                PublishCurrent(_generation);
                return true;
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
                if (receiver.Online && receiver.Complete && (!receiver.IsSoftwareGroup || receiver.HasDistinctSoftwareMembers))
                    _discoveredReceiver = receiver;
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
            lock (_sync) { if (NeedsCleanup || !OwnsDiscoverySnapshot(expected) || _receiver?.IsSoftwareGroup != true) return false; }
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
    private bool OwnsRemoteCommand(RemoteCommandAuthority authority) =>
        !_passiveDisposed && _generation == authority.Owner && _automaticIntent == authority.Intent
        && _sessionId == authority.SessionId && _remoteTransport == authority.Transport
        && _transportStreaming && _snapshot.State == PlaybackState.Streaming
        && _lifetime?.IsCancellationRequested == false;

    internal bool IsRemoteCommandCurrent(RemoteCommandAuthority authority)
    {
        lock (_sync) return OwnsRemoteCommand(authority);
    }

    internal async Task<bool> TryToggleRemoteMuteAsync(RemoteCommandAuthority authority)
    {
        Task admission;
        lock (_sync)
        {
            if (!OwnsRemoteCommand(authority)) return false;
            // Register before disposal can close admission and join the serial queue.
            admission = _serial.WaitAsync();
        }
        await admission.ConfigureAwait(false);
        try
        {
            IEngineConnection connection;
            double gain;
            lock (_sync)
            {
                if (!OwnsRemoteCommand(authority) || _connection is not { } current || _receiver is not { } receiver) return false;
                connection = current;
                _muted = !_muted;
                gain = Gain(receiver);
            }
            await SendGainAsync(connection, authority.SessionId, gain).ConfigureAwait(false);
            return true;
        }
        finally { _serial.Release(); }
    }
    private static object StandbyParameters(AppSettings settings, string receiverId) => new { enabled = settings.StandbyEnabled, silence_seconds = settings.ReadOptions(receiverId).StandbySeconds ?? settings.StandbySilenceSeconds };
    private async Task SendStandbyAsync()
    {
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) return;
        await _equalizerWriter.WaitAsync().ConfigureAwait(false);
        try
        {
            IEngineConnection? connection; string? session; (bool Enabled, double Seconds) policy;
            lock (_sync)
            {
                if (_passiveDisposed || !_startSent || _receiver is null) return;
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
        await SendGainAsync(connection, session, Gain(_receiver)).ConfigureAwait(false);
    }
    private async Task SendGainAsync(IEngineConnection connection, string session, double gain)
    {
        try { await connection.SendAsync(session, "set_gain", new { gain }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { log?.Invoke(L.Format("Volume command deferred to the next session: {0}", error.Message)); }
    }
    /// <summary>Forward Now Playing to the engine, which turns it into the
    /// receiver's `SET_PARAMETER` progress and DMAP tag payloads. Null or empty
    /// values are ignored; use <see cref="ClearNowPlayingAsync"/> when the source
    /// goes away.</summary>
    public async Task PublishNowPlayingAsync(NowPlaying? now, CancellationToken cancellation = default)
    {
        if (now is null || now.IsEmpty) return;
        lock (_sync)
        {
            if (_passiveDisposed) return;
            _nowPlaying = now;
            _nowPlayingSampledAt = _clock.GetTimestamp();
        }
        await SendNowPlayingAsync(cancellation).ConfigureAwait(false);
    }
    /// <summary>The Now Playing source went away: forget the cached value so a later
    /// session does not show it, and give a live engine an empty value in place of
    /// its copy. The engine sends nothing to the receiver for an empty value, so the
    /// current display is kept, but a transport it rebuilds (standby resume) no
    /// longer re-sends the old track.</summary>
    public async Task ClearNowPlayingAsync(CancellationToken cancellation = default)
    {
        lock (_sync)
        {
            if (_passiveDisposed || (_nowPlaying is null && !_nowPlayingMayBeSet)) return;
            _nowPlaying = null;
        }
        await SendNowPlayingAsync(cancellation).ConfigureAwait(false);
    }
    /// <summary>Send to the current engine when its hello advertised <c>now_playing</c>.</summary>
    private async Task SendNowPlayingAsync(CancellationToken cancellation)
    {
        lock (_sync) { if (!_nowPlayingSupported || !_startSent) return; }
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) return;
        try
        {
            await _nowPlayingWriter.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                IEngineConnection connection;
                string session;
                NowPlaying? now;
                long sampledAt;
                lock (_sync)
                {
                    if (_passiveDisposed || !_nowPlayingSupported || !_startSent
                        || _connection is not { } current || _sessionId is not { } currentSession) return;
                    now = _nowPlaying;
                    if (now is null && !_nowPlayingMayBeSet) return;
                    if (now is not null) _nowPlayingMayBeSet = true;
                    sampledAt = _nowPlayingSampledAt;
                    connection = current;
                    session = currentSession;
                }
                // The native mailbox belongs to the started process, including
                // while it rebuilds its transport and the UI says Connecting.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await connection.SendAsync(session, "set_now_playing", new
                {
                    title = now?.Title ?? "", artist = now?.Artist ?? "", album = now?.Album ?? "", genre = now?.Genre ?? "",
                    duration_ms = now?.DurationMs ?? 0, position_ms = now?.PositionAfter(_clock.GetElapsedTime(sampledAt)) ?? 0,
                    playing = now?.Playing ?? false
                }, timeout.Token).ConfigureAwait(false);
                if (now is null)
                    lock (_sync) { if (ReferenceEquals(connection, _connection)) _nowPlayingMayBeSet = false; }
            }
            finally { _nowPlayingWriter.Release(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log?.Invoke(L.Format("Now Playing deferred to the next session: {0}", error.Message));
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>The current streaming engine's redacted summary, or the last
    /// accepted historical capture when playback is inactive.</summary>
    public async Task<JsonElement?> DiagnosticSnapshotAsync(CancellationToken cancellation = default)
        => (await CaptureDiagnosticsAsync(cancellation).ConfigureAwait(false)).Summary;

    /// <summary>Capture a summary and counter history from the same playback scope.
    /// A failed or superseded request retains its request-time history.</summary>
    public async Task<DiagnosticCapture> CaptureDiagnosticsAsync(CancellationToken cancellation = default)
    {
        using var operation = TryAdmitPassiveOperation();
        if (operation is null) { lock (_sync) return AvailableDiagnosticsLocked(); }
        await _diagnosticWriter.WaitAsync(cancellation).ConfigureAwait(false);
        PendingDiagnostic? pending = null;
        DiagnosticCapture fallback = new(null, null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            ICorrelatedEngineConnection connection;
            lock (_sync)
            {
                fallback = new(null, FreezeCounterHistoryLocked());
                if (_passiveDisposed || !_snapshot.IsActive) return AvailableDiagnosticsLocked();
                if (_connection is not ICorrelatedEngineConnection current || _sessionId is not { } session
                    || !_transportStreaming || _snapshot.State != PlaybackState.Streaming || !_diagnosticsSupported)
                    return fallback;
                connection = current;
                var scope = new DiagnosticScope(current, session, _generation, _remoteTransport, _nativeSessionEpoch);
                pending = _diagnosticReply = new(Guid.NewGuid().ToString("N"), scope, fallback, timeout.Token,
                    new(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            using var canceled = timeout.Token.Register(() => pending.Reply.TrySetResult(pending.Fallback));
            await connection.SendCorrelatedAsync(pending.Id, pending.Scope.Session, "diag_summary", null, timeout.Token).ConfigureAwait(false);
            var result = await pending.Reply.Task.ConfigureAwait(false);
            lock (_sync)
            {
                if (timeout.IsCancellationRequested || !DiagnosticScopeCurrentLocked(pending.Scope)
                    || result.Summary is { } summary && !DiagnosticEpochMatchesLocked(summary)) return fallback;
                if (result.Summary is not null) _lastDiagnostic = result;
                return result;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            return fallback;
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_diagnosticReply, pending)) _diagnosticReply = null; }
            _diagnosticWriter.Release();
        }
    }

    private sealed record DiagnosticScope(IEngineConnection Connection, string Session, long Owner, long Transport, long? NativeEpoch);
    private sealed record PendingDiagnostic(string Id, DiagnosticScope Scope, DiagnosticCapture Fallback, CancellationToken Cancellation,
        TaskCompletionSource<DiagnosticCapture> Reply);

    private bool DiagnosticScopeCurrentLocked(DiagnosticScope scope) =>
        !_passiveDisposed && _generation == scope.Owner && ReferenceEquals(_connection, scope.Connection)
        && _sessionId == scope.Session && _remoteTransport == scope.Transport && _transportStreaming
        && _snapshot.State == PlaybackState.Streaming && _lifetime?.IsCancellationRequested == false
        && (scope.NativeEpoch is null || scope.NativeEpoch == _nativeSessionEpoch);

    private DiagnosticCapture AvailableDiagnosticsLocked() =>
        !_snapshot.IsActive && _lastDiagnostic is { } cached ? cached : new(null, FreezeCounterHistoryLocked());

    private IReadOnlyList<PlaybackCounterDelta>? FreezeCounterHistoryLocked() =>
        _diagnostics.CounterDeltaHistory is not { } history ? null : Array.AsReadOnly(history.Select(report => report with
        {
            Counters = report.Counters is null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, ulong>(
                new Dictionary<string, ulong>(report.Counters)),
            Members = report.Members is null ? null : Array.AsReadOnly(report.Members.Select(member => member with
            {
                Counters = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ulong>(
                    new Dictionary<string, ulong>(member.Counters))
            }).ToArray())
        }).ToArray());

    private void AcceptDiagnosticLocked(IEngineConnection connection, JsonElement item)
    {
        if (_diagnosticReply is not { } pending || !ReferenceEquals(pending.Scope.Connection, connection)
            || item.Text("id") != pending.Id) return;
        if (pending.Cancellation.IsCancellationRequested || !DiagnosticScopeCurrentLocked(pending.Scope) || !DiagnosticEpochMatchesLocked(item))
        {
            pending.Reply.TrySetResult(pending.Fallback);
            return;
        }
        var capture = new DiagnosticCapture(item.Clone(), FreezeCounterHistoryLocked());
        pending.Reply.TrySetResult(capture);
    }

    private bool DiagnosticEpochMatchesLocked(JsonElement item)
    {
        if (!item.TryGetProperty("session", out var summary) || summary.ValueKind != JsonValueKind.Object) return false;
        var epoch = summary.Integer("session_epoch");
        if (summary.TryGetProperty("session_epoch", out var value) && value.ValueKind != JsonValueKind.Null && epoch is not > 0) return false;
        return _nativeSessionEpoch is null || epoch == _nativeSessionEpoch;
    }

    private void ObserveNativeEpochLocked(long? epoch)
    {
        if (epoch is not > 0 || epoch == _nativeSessionEpoch || _nativeSessionEpoch is { } previous && epoch < previous) return;
        _nativeSessionEpoch = epoch;
        _lastDiagnostic = null;
    }
    private int _adaptiveBase;
    private int _adaptiveInitial;
    private bool _confirmedLeadSupported;
    private async Task CheckAdaptiveAsync(IEngineConnection connection, string session, long epoch, CancellationToken cancellation)
    {
        AdaptiveAction? action;
        lock (_sync)
        {
            if (epoch != _generation || !ReferenceEquals(_connection, connection) || AdaptivePlan() is not { } plan) return;
            action = _adaptive.Evaluate(plan, _confirmedLeadSupported);
        }
        if (action is null) return;
        if (action.Rebuild) throw new AdaptiveRebuild(action.Target, action.Reason);
        log?.Invoke($"Adaptive latency requested: session={session}; stream_epoch={action.SessionEpoch}; sequence={action.Sequence}; target_ms={action.Target}; extra_ms={action.ExtraMs}; reason={action.Reason}");
        PublishCurrent(epoch);
        using var timeout = new CancellationTokenSource(AdaptiveLatency.ConfirmationTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try { await connection.SendAsync(session, "set_lead", new { ms = action.ExtraMs, sequence = action.Sequence, session_epoch = action.SessionEpoch }, linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                _adaptive.Reject(action.Sequence, action.SessionEpoch, error.Message);
                if (AdaptivePlan() is { } plan) _adaptive.Evaluate(plan, _confirmedLeadSupported);
            }
            throw new AdaptiveRebuild(action.Target, "lead command failed: " + error.Message);
        }
    }
    private void AcceptLatency(JsonElement item)
    {
        if (LatencyRuntime.Parse(item) is not { } runtime) return;
        bool confirmed;
        lock (_sync) confirmed = _adaptive.Accept(runtime);
        if (confirmed) log?.Invoke($"Adaptive latency applied: stream_epoch={runtime.SessionEpoch}; sequence={runtime.Sequence}; baseline_ms={runtime.BaselineMs}; extra_ms={runtime.ExtraMs}; effective_ms={runtime.EffectiveMs}; ceiling_ms={runtime.CeilingMs}; observation_seconds=10");
    }
    private LatencyPolicy.Plan? AdaptivePlan()
    {
        lock (_sync)
        {
            if (_receiver is not { } receiver || _snapshot.State != PlaybackState.Streaming || LatencyPolicy.Forced(_settings, receiver)) return null;
            var targets = receiver.IsSoftwareGroup ? receiver.Peers : [receiver];
            if (targets.Any(r => (_settings.ReadOptions(r.Id).LatencyMode ?? _settings.LatencyMode) != LatencyPolicy.Mode)) return null;
            var negotiated = _diagnostics.Negotiated ?? [];
            if (negotiated.Count == 0 || negotiated.Any(n => n.Text("transport") == "buffered")) return null;
            return LatencyPolicy.For(receiver, negotiated[0].Text("codec"), negotiated[0].Text("timing") == "ptp");
        }
    }
    private static bool UsesAdaptive(AppSettings settings, Receiver receiver) => !LatencyPolicy.Forced(settings, receiver)
        && (receiver.IsSoftwareGroup ? receiver.Peers : [receiver]).All(r => (settings.ReadOptions(r.Id).LatencyMode ?? settings.LatencyMode) == LatencyPolicy.Mode);
    private async Task RunStreamAsync(Receiver receiver, long epoch, SessionStartIntent intent, CancellationToken cancellation)
    {
        var attempts = 0;
        var diagnosticAttempt = 0;
        var counterHistory = new PlaybackCounterHistory();
        var recoveryHealth = new RecoveryHealth(_clock);
        // Q43 companion record: how many reconnects this playback run had already
        // done when the next attempt started, and the whole fault story (first,
        // count and the compact sequence) instead of only the latest reason.
        var reconnects = 0;
        var transportFaults = new List<string>();
        var recovering = false;
        while (!cancellation.IsCancellationRequested)
        {
            // A new process must not replace a mute obligation left by cleanup.
            EnsureMuteReady(_settings);
            var attemptIntent = intent;
            intent = SessionStartIntent.Automatic;
            lock (_sync)
            {
                if (epoch != _generation || _receiver is null) return;
                if (attempts > 0) _receiver = RestartReceiver(_receiver, _settings);
                receiver = _receiver;
            }
            var startedAt = 0L;
            var hasStreamed = false;
            var healthUptimeMs = 0L;
            var advisoryUptimeMs = 0L;
            HealthSample? previousHealth = null;
            try
            {
                var settings = _settings;
                if (LatencyPolicy.ForceConflict(settings, receiver) is { } conflict)
                    throw new EngineFailure(new("forced_latency_conflict", "", "setup", conflict, false));
                var forceLatency = LatencyPolicy.Forced(settings, receiver);
                var peers = new List<object>();
                string? volumeHost = null;
                var hosts = new Dictionary<string, string>();
                foreach (var member in receiver.Peers.OrderByDescending(p => p.IsLeader))
                {
                    var host = await ReceiverAddress.ResolveIPv4Async(member.Address, cancellation).ConfigureAwait(false);
                    volumeHost ??= host;
                    hosts[member.Id] = host;
                    peers.Add(new { receiver_id = member.Id, host, port = member.Port, codecs = member.Codecs.Select(x => (int)x).ToArray(), profile = PeerProfile(receiver, member, settings) });
                }
                await using var connection = factory.Open();
                var currentAttempt = ++diagnosticAttempt;
                counterHistory.End();
                recoveryHealth.BeginAttempt(hosts.Values);
                var session = Guid.NewGuid().ToString("N");
                SetConnection(connection, session);
                lock (_sync) _adaptive.BeginAttempt(_settings.Latency(receiver));
                try
                {
                    lock (_sync) { if (epoch == _generation) { _diagnostics = _diagnostics with { Transport = null, Warnings = [], Negotiated = [], ClockStatus = null }; _warnings.Clear(); _deviceVolume = new(); _memberVolumes.Clear(); _memberHosts.Clear(); foreach (var pair in hosts) _memberHosts[pair.Key] = pair.Value; _volumeEdit?.Cancel(); _snapshot = _snapshot with { Metrics = null }; } }
                    Publish(epoch, PlaybackState.Connecting, attempts > 0 ? L.Format("Reconnecting ({0}/{1})", attempts, settings.MaxReconnectAttempts) : L.Get("Connecting…"), isRetrying: attempts > 0);
                    var deadline = DateTime.UtcNow + _timing.Connect;
                    await connection.SendAsync(session, "hello", null, cancellation).ConfigureAwait(false);
                    var hello = await ReadEventAsync(connection, session, _timing.Connect, cancellation).ConfigureAwait(false);
                    lock (_sync) { if (epoch == _generation) _soundGlowSupported = EngineHandshake.HasCapability(hello, "sound_glow_test"); }
                    _confirmedLeadSupported = EngineHandshake.HasCapability(hello, "adaptive_lead_v2");
                    lock (_sync)
                    {
                        _nowPlayingSupported = EngineHandshake.HasCapability(hello, "now_playing");
                        _diagnosticsSupported = EngineHandshake.HasCapability(hello, "diagnostics");
                    }
                    EngineHandshake.RequireStreamingCapabilities(hello);
                    EngineHandshake.RequireCapabilities(hello, "standby_release");
                    var restoreVolume = EngineHandshake.HasCapability(hello, "initial_volume");
                    if (!EngineHandshake.HasCapability(hello, "receiver_ownership_guard"))
                        throw new EngineFailure(new("ownership_guard_required", "", "ipc", L.Get("Incompatible engine protocol version."), false));
                    if (forceLatency && !EngineHandshake.HasCapability(hello, "forced_latency"))
                        throw new EngineFailure(new("forced_latency_unsupported", "", "setup", L.Get("This audio engine does not support forced latency. Update the engine or turn off forced latency."), false));
                    if (settings.CaptureMode == "application") EngineHandshake.RequireCapabilities(hello, "application_capture");
                    if (receiver.IsSoftwareGroup) EngineHandshake.RequireCapabilities(hello, "member_volume");
                    if (receiver.IsMultiRoom) EngineHandshake.RequireCapabilities(hello, "multi_room");
                    var capabilities = await ReceiverCapabilityCatalog.QueryConnectionAsync(connection, session, hello, receiver, cancellation, hosts).ConfigureAwait(false);
                    var selected = capabilities.Select(id => ProfileOptions(receiver, receiver.Peers.Single(p => p.Id == id), settings), settings.SampleRate(receiver));
                    if (capabilities.Members.Count > 0 && selected.Count == 0) throw new InvalidOperationException(L.Get("The receiver does not support this stream profile. Choose Automatic or a supported option in Receivers."));
                    var sampleRate = selected.FirstOrDefault()?.Profile.SampleRate ?? (int.TryParse(settings.SampleRate(receiver), out var explicitRate) ? explicitRate : 44100);
                    peers.Clear();
                    _adaptiveInitial = receiver.Peers.Max(member => InitialLatency(receiver, member, settings, selected.FirstOrDefault(s => s.ReceiverId == member.Id)?.Profile));
                    var adaptiveMode = UsesAdaptive(settings, receiver);
                    _adaptiveBase = adaptiveMode ? Math.Max(_adaptiveInitial, _adaptive.RecoveryTarget ?? 0) : _adaptiveInitial;
                    lock (_sync) _adaptive.BeginAttempt(_adaptiveBase);
                    foreach (var member in receiver.Peers.OrderByDescending(p => p.IsLeader))
                    {
                        var peer = new Dictionary<string, object?>
                        {
                            ["receiver_id"] = member.Id, ["host"] = hosts[member.Id], ["port"] = member.Port,
                            ["codecs"] = member.Codecs.Select(x => (int)x).ToArray(),
                            ["profile"] = PeerProfile(receiver, member, settings, selected.FirstOrDefault(s => s.ReceiverId == member.Id)?.Profile, adaptiveMode ? _adaptiveBase : null)
                        };
                        // Only send the field when the engine understands it, so an
                        // older engine keeps reading the receiver's own volume.
                        if (restoreVolume && RememberedVolume(receiver, member, settings) is { } volume) peer["volume"] = volume;
                        peers.Add(peer);
                    }
                    await _equalizerWriter.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        var groupMode = receiver.PlaybackGroupMode;
                        var startParameters = JsonSerializer.SerializeToNode(new { peers, group_mode = groupMode, group_id = receiver.IsGroup && !receiver.IsSoftwareGroup ? receiver.StereoId : null, source = settings.CaptureMode == "application" ? "application" : "loopback", capture_application_path = settings.CaptureMode == "application" ? settings.CaptureApplicationPath : null, capture_process_id = settings.CaptureMode == "application" ? settings.CaptureApplicationInstance?.ProcessId : null, capture_process_started_at = settings.CaptureMode == "application" ? settings.CaptureApplicationInstance?.StartedAt : null, standby = StandbyParameters(settings, receiver.Id), duration_ms = 0, latency_ms = _adaptiveBase, gain = Gain(receiver), timing = "ptp", capture_endpoint = settings.EffectiveEndpoint, sample_rate = sampleRate, equalizer = EffectiveEqualizer().WireParameters() })!.AsObject();
                        if (forceLatency) startParameters["force_latency"] = true;
                        startParameters["intent"] = attemptIntent == SessionStartIntent.Manual ? "manual" : "automatic";
                        if (adaptiveMode && _confirmedLeadSupported)
                        {
                            startParameters["adaptive_latency"] = true;
                            startParameters["adaptive_initial_latency_ms"] = _adaptiveInitial;
                        }
                        await connection.SendAsync(session, "start", startParameters, cancellation).ConfigureAwait(false);
                        lock (_sync) { if (epoch == _generation) _snapshot = _snapshot with { StreamRate = sampleRate }; }
                        lock (_sync) { if (epoch == _generation && ReferenceEquals(_connection, connection)) _startSent = true; }
                    }
                    finally { _equalizerWriter.Release(); }
                    PublishCurrent(epoch);
                    await SendStandbyAsync().ConfigureAwait(false);
                    await SendEqualizerAsync(cancellation: cancellation).ConfigureAwait(false);
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await CheckAdaptiveAsync(connection, session, epoch, cancellation).ConfigureAwait(false);
                        var wait = startedAt == 0 ? deadline - DateTime.UtcNow : _timing.Read;
                        // System timers can wake before the monotonic deadline.
                        // Recheck a lead deadline after waking instead of turning
                        // a missing acknowledgement into a generic read fault.
                        var waitingForLead = false;
                        if (_adaptive.ConfirmationRemaining is { } remaining && remaining < wait)
                        {
                            waitingForLead = true;
                            wait = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)));
                        }
                        if (wait <= TimeSpan.Zero) throw new TimeoutException(L.Get("Timed out connecting to the audio device."));
                        JsonElement item;
                        try { item = await ReadEventAsync(connection, session, wait, cancellation).ConfigureAwait(false); }
                        catch (TimeoutException) when (waitingForLead)
                        {
                            await CheckAdaptiveAsync(connection, session, epoch, cancellation).ConfigureAwait(false);
                            continue;
                        }
                        var kind = item.Text("event");
                        if (kind.StartsWith("sound_glow_", StringComparison.Ordinal))
                        {
                            lock (_sync)
                            {
                                if (epoch != _generation || item.Integer("sequence") is { } sequence
                                    && (!_transportStreaming || _soundGlowTransport != _remoteTransport || sequence != _soundGlowSequence)) continue;
                            }
                            log?.Invoke("Sound Glow: " + item.GetRawText());
                            SoundGlowFeedback?.Invoke(item.Clone());
                            continue;
                        }
                        if (kind == "error") throw new EngineFailure(EngineNotice.Parse(item));
                        if (kind == "stopped") throw new IOException(L.Get("The audio session ended unexpectedly."));
                        if (kind == "diag_summary")
                        {
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                AcceptDiagnosticLocked(connection, item);
                            }
                        }
                        if (kind == "peer_info")
                        {
                            // Authoritative `/info` evidence; manual receivers have no mDNS TXT record.
                            log?.Invoke($"Receiver info: host {item.Text("host")}; model {item.Text("model", "unknown")}; source_version {item.Text("source_version", "unknown")}");
                        }
                        if (kind == "negotiated")
                        {
                            lock (_sync) { if (epoch != _generation) continue; _diagnostics = _diagnostics with { Negotiated = (_diagnostics.Negotiated ?? []).Where(n => item.Text("receiver_id").Length > 0 ? n.Text("receiver_id") != item.Text("receiver_id") : n.Text("host") != item.Text("host")).Append(item.Clone()).ToArray() }; }
                            // The handshake alone does not prove that the receiver can render
                            // the stream; this line records the codec, transport, timing and
                            // clock role needed to diagnose a silent session.
                            log?.Invoke($"Receiver negotiated: {item.Text("receiver_id", item.Text("host"))}; host {item.Text("host")}; codec {item.Text("codec")}; transport {item.Text("transport")}; timing {item.Text("timing")}; clock_role {item.Text("clock_role")}; {item.Integer("sample_rate")} Hz");
                            if (item.Number("effective_latency_ms") is { } baseline) lock (_sync) _adaptive.Negotiated(baseline);
                        }
                        if (kind is "lead_changed" or "lead_error")
                        {
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                if (kind == "lead_error") _adaptive.Reject(item.Integer("sequence"), item.Integer("session_epoch"), item.Text("message"));
                            }
                            if (kind == "lead_changed") AcceptLatency(item);
                            PublishCurrent(epoch);
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
                            recovering = false;
                            AcceptLatency(item);
                            counterHistory.Begin(currentAttempt, item.Integer("session_epoch"));
                            startedAt = Stopwatch.GetTimestamp();
                            hasStreamed = true;
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                // Standby can replace the live transport inside the same IPC session.
                                if (!_transportStreaming || _snapshot.State != PlaybackState.Streaming)
                                {
                                    ++_remoteTransport;
                                    _nativeSessionEpoch = null;
                                    _lastDiagnostic = null;
                                    if (_diagnosticReply is { } pending) pending.Reply.TrySetResult(pending.Fallback);
                                }
                                ObserveNativeEpochLocked(item.Integer("session_epoch"));
                                _transportStreaming = true;
                            }
                            await ApplyLocalMuteAsync().ConfigureAwait(false);
                            log?.Invoke($"Streaming started: {receiver.Id}; target latency {_adaptiveBase} ms; reconnect attempts {attempts}");
                            Publish(epoch, PlaybackState.Streaming);
                            await SendNowPlayingAsync(cancellation).ConfigureAwait(false);
                        }
                        else if (kind == "standby")
                        {
                            counterHistory.End();
                            recovering = false;
                            recoveryHealth.ResetStability();
                            lock (_sync) _adaptive.Suspend();
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
                            counterHistory.End();
                            recoveryHealth.BeginAttempt(hosts.Values);
                            lock (_sync) _adaptive.Resume(_adaptiveInitial);
                            startedAt = 0; deadline = DateTime.UtcNow + _timing.Connect;
                            // Native also emits resuming before its first stream.
                            // If this worker is already retrying, keep that status and fault.
                            var current = Snapshot;
                            var status = current.IsRetrying ? current.Message
                                : L.Get(hasStreamed ? "Audio detected; reconnecting…" : "Audio detected; connecting…");
                            Publish(epoch, PlaybackState.Connecting, status, isRetrying: current.IsRetrying);
                        }
                        else if (kind == "device_volume" && AcceptsVolumeReads() && receiver.IsSoftwareGroup)
                        {
                            lock (_sync) { if (epoch != _generation) continue; var id = _memberHosts.FirstOrDefault(p => p.Value == item.Text("host")).Key; if (id is not null) _memberVolumes[id] = (_memberVolumes.GetValueOrDefault(id) ?? new()).Receive(item); }
                            PublishCurrent(epoch);
                        }
                        else if (kind == "device_volume" && AcceptsVolumeReads() && item.Text("host") == volumeHost)
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
                        else if (kind == "remote_command" && startedAt != 0)
                        {
                            RemoteCommandAuthority authority;
                            lock (_sync)
                            {
                                if (epoch != _generation || _sessionId != session) continue;
                                authority = new(epoch, _automaticIntent, session, _remoteTransport);
                            }
                            foreach (var command in RemoteCommand.Parse(item))
                            {
                                log?.Invoke($"Remote command from {command.Host ?? "receiver"}: {command.Name}{(command.Volume is { } level ? $" (volume {level:0.###})" : "")}");
                                try
                                {
                                    RemoteCommandReceived?.Invoke(command);
                                    OwnedRemoteCommandReceived?.Invoke(command, authority);
                                }
                                catch (Exception error) { log?.Invoke($"Remote command {command.Name} failed: {error.Message}"); }
                            }
                        }
                        else if (kind == "warning" && (startedAt != 0 || item.Text("code") == "forced_latency_below_minimum"))
                        {
                            var notice = EngineNotice.Parse(item);
                            if (notice.Channel is "feedback" or "clock" or "preemption" && item.Boolean("recovered") != true)
                                recoveryHealth.ResetStability();
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                var key = notice.Host + ":" + notice.Channel;
                                if (item.Boolean("recovered") == true) _warnings.Remove(key); else _warnings[key] = notice;
                                _diagnostics = _diagnostics with { Warnings = _warnings.Values.ToArray() };
                            }
                            log?.Invoke($"Engine warning: {item}");
                            if (AdaptivePlan() is not null) lock (_sync) _adaptive.Recovery(item.Text("code"));
                            // Republish so the warning reaches the diagnostics snapshot. A
                            // takeover is never a warning: under receiver_ownership_guard the
                            // engine ends the session with a terminal `preempted` error and
                            // never reclaims the receiver itself.
                            var current = Snapshot;
                            Publish(epoch, current.State, current.Message, isRetrying: current.IsRetrying);
                        }
                        else if (kind == "transport_metrics" && startedAt != 0)
                        {
                            var receivedAt = _clock.GetUtcNow();
                            var metrics = TransportMetrics.Parse(item);
                            if (recoveryHealth.Observe(metrics))
                            {
                                attempts = 0;
                                log?.Invoke("Session retry budget reset reason=receiver_healthy_60s");
                            }
                            AcceptLatency(item);
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                if (_snapshot.State == PlaybackState.Streaming) ObserveNativeEpochLocked(metrics.SessionEpoch);
                                var history = counterHistory.ObserveTransport(receivedAt, metrics);
                                _diagnostics = _diagnostics with { Transport = metrics, CounterDeltaHistory = history ?? _diagnostics.CounterDeltaHistory };
                            }
                            // One health line per minute in every latency mode; the
                            // adaptive ladder keeps its own line below.
                            if (metrics.SessionUptimeMs is { } reportedUptimeMs && reportedUptimeMs >= 0)
                            {
                                var uptimeMs = (long)reportedUptimeMs;
                                // A reconnect restarts the engine's uptime; start over.
                                if (uptimeMs < healthUptimeMs) { healthUptimeMs = 0; advisoryUptimeMs = 0; previousHealth = null; }
                                if (SessionHealth.Due(uptimeMs, healthUptimeMs))
                                {
                                    healthUptimeMs = uptimeMs;
                                    var sample = HealthSample.From(metrics, Snapshot.Metrics, Snapshot.TargetLatency);
                                    // Fixed targets cannot raise their own headroom, so the
                                    // log states the case instead of changing playback.
                                    var advisory = SessionHealth.LooksTight(sample, previousHealth)
                                        && AdaptivePlan() is null && SessionHealth.AdvisoryDue(uptimeMs, advisoryUptimeMs);
                                    previousHealth = sample;
                                    if (advisory) advisoryUptimeMs = uptimeMs;
                                    QueueHealthLog(sample.Describe(), advisory);
                                }
                            }
                            if (AdaptivePlan() is not null)
                            {
                                bool fresh; lock (_sync) fresh = _adaptive.Observe(metrics);
                                if (fresh && metrics.SessionUptimeMs is { } uptime && (int)(uptime / 1000) % 10 == 0)
                                    log?.Invoke($"Adaptive latency health: stream_epoch={metrics.SessionEpoch}; effective_ms={_adaptive.Current}; p99_ms={metrics.IntervalLatenessP99Ms}; active_underruns={metrics.UnderrunsActive}; idle_underruns={metrics.UnderrunsIdle}; rebuilds={_adaptive.Rebuilds}");
                            }
                            var current = Snapshot; Publish(epoch, current.State, current.Message, isRetrying: current.IsRetrying);
                        }
                        else if (kind == "capture_metrics" && item.TryGetProperty("metrics", out var metrics))
                        {
                            var receivedAt = _clock.GetUtcNow();
                            var parsed = new StreamMetrics(metrics.Number("capture_to_send_p95_ms"), metrics.Number("max_queue_age_ms"), metrics.Integer("underrun_packets"), metrics.Integer("dropped_frames"), metrics.Integer("input_rate") is { } rate ? (int)rate : null,
                                metrics.Integer("rebinds"), metrics.Integer("silent_probes"), metrics.Integer("rebind_storms"))
                            {
                                PaddedFrames = metrics.Unsigned("padded_frames"),
                                FullyPaddedPackets = metrics.Unsigned("fully_padded_packets")
                            };
                            lock (_sync)
                            {
                                if (epoch != _generation) continue;
                                if (counterHistory.ObserveCapture(receivedAt, parsed) is { } history)
                                    _diagnostics = _diagnostics with { CounterDeltaHistory = history };
                            }
                            var current = Snapshot;
                            Publish(epoch, current.State, current.Message, metrics: parsed, isRetrying: current.IsRetrying);
                        }
                        await CheckAdaptiveAsync(connection, session, epoch, cancellation).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ClearConnection(connection);
                    lock (_sync)
                    {
                        if (_diagnosticReply is { } pending && ReferenceEquals(pending.Scope.Connection, connection))
                            pending.Reply.TrySetResult(pending.Fallback);
                    }
                    await RequestStopAsync(connection, session).ConfigureAwait(false);
                    // One desktop row per IPC session, written before the catch
                    // decides whether to reconnect: the engine's row for this
                    // session already exists or is being written now. The join key
                    // is this id; the engine appends "-<session_epoch>".
                    _sessionRecords(DesktopSessionLog.FromFaults(session, reconnects, transportFaults));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (EngineShutdownException) { throw; }
            catch (Exception error)
            {
                await RestoreAudioAsync().ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) return;
                if (error is AdaptiveRebuild rebuild)
                {
                    if (!_adaptive.ConsumeRebuild(_settings.MaxReconnectAttempts))
                    {
                        var exhausted = new EngineFailure(new("adaptive_recovery_exhausted", "", "scheduler", L.Get("Adaptive recovery could not stabilize playback. Reconnect to try again."), false));
                        if (RecordFailure(epoch, exhausted) is { } failure) Publish(epoch, PlaybackState.Error, failure.Detail, failure: failure);
                        return;
                    }
                    log?.Invoke($"Adaptive latency rebuilding: target_ms={rebuild.Target}; reason={rebuild.Message}; rebuild={_adaptive.Rebuilds}/{_settings.MaxReconnectAttempts}");
                }
                ++attempts;
                if (RecordFailure(epoch, error) is not { } notice) return;
                Publish(epoch, FailureState(notice), FailureMessage(receiver, notice), failure: notice);
                // A preempted receiver belongs to another controller now; retrying
                // would fight it. The user gets an explicit reconnect action instead.
                if (notice.Code is "preempted" or "ownership_unconfirmed")
                {
                    log?.Invoke($"Session automatic recovery blocked; code={notice.Code}; manual_reconnect_required=true");
                    return;
                }
                var recoveryAllowed = _settings.ForceReconnect || receiver.IsSoftwareGroup || notice.Code is "timeline_expired" or "clock_lost" or "adaptive_rebuild" || (recovering && (notice.Code is "host_error" or "network_error" || notice.Retryable == true));
                if (!recoveryAllowed || attempts > _settings.MaxReconnectAttempts || notice.Retryable == false || notice.Retryable is null && IsAuthenticationFailure(error.Message)) return;
                recovering = true;
                // This is the one reconnect notion the desktop already has; the
                // companion record only reports it, it does not add a second one.
                ++reconnects;
                transportFaults.Add(notice.Code);
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(_timing.Retry.TotalMilliseconds * Math.Pow(2, attempts - 1), 8000));
            log?.Invoke($"Session reconnect reason=transport_fault; code={_diagnostics.LastFault?.Code}; attempt={attempts}; delay_ms={delay.TotalMilliseconds}");
            Publish(epoch, PlaybackState.Connecting, L.Format("Disconnected; reconnecting ({0}/{1})", attempts, _settings.MaxReconnectAttempts), isRetrying: true);
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
                await connection.SendAsync(session, "pair", new { password_required = member.RequiresPassword, peer = new { host = await ReceiverAddress.ResolveIPv4Async(member.Address, cancellation).ConfigureAwait(false), port = member.Port } }, cancellation).ConfigureAwait(false);
                await using var events = new PairingEvents(this, connection, session, cancellation);
                while (true)
                {
                    var item = await events.ReadAsync(_timing.Connect, cancellation).ConfigureAwait(false);
                    switch (item.Text("event"))
                    {
                        case "pin_required":
                        case "password_required":
                            {
                                var passwordPrompt = item.Text("event") == "password_required";
                                var input = await RequestPairingInputAsync(events, member with { RequiresPassword = passwordPrompt }, requestPin, cancellation).ConfigureAwait(false);
                                if (input.Paired) goto Paired;
                                var pin = passwordPrompt ? input.Pin : PairingPin.Normalize(input.Pin);
                                if (passwordPrompt ? !PasswordInput.IsValid(pin) : pin is null) { Publish(epoch, PlaybackState.Idle, L.Get("Pairing cancelled.")); throw new PairCancelledException(); }
                                cancellation.ThrowIfCancellationRequested();
                                await connection.SendAsync(session, "pair_pin", new { pin }, cancellation).ConfigureAwait(false);
                            }
                            break;
                        case "paired": goto Paired;
                        case "error": throw new EngineFailure(EngineNotice.Parse(item));
                        case "stopped": throw new IOException(L.Get("The audio session ended unexpectedly."));
                    }
                }
            Paired:;
            }
            finally { ClearConnection(connection); await RequestStopAsync(connection, session).ConfigureAwait(false); }
        }
    }
    private async Task<(string? Pin, bool Paired)> RequestPairingInputAsync(PairingEvents events, Receiver member,
        Func<Receiver, CancellationToken, Task<string?>> requestPin, CancellationToken cancellation)
    {
        using var promptDeadline = new CancellationTokenSource(_timing.Pin, _clock);
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, promptDeadline.Token);
        Task<string?>? prompt = null;
        try
        {
            // The same single read survives input submission. Canceling a pending
            // StreamReader read here could consume a reply without delivering it.
            var pending = events.Pending;
            prompt = requestPin(member, promptCancellation.Token);
            while (true)
            {
                await Task.WhenAny(prompt, pending).WaitAsync(promptCancellation.Token).ConfigureAwait(false);
                // A terminal event already observed by the reader wins over a
                // concurrently completed prompt, before any pair_pin is sent.
                if (pending.IsCompleted)
                {
                    var item = await events.ReadAsync(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
                    switch (item.Text("event"))
                    {
                        case "error": throw new EngineFailure(EngineNotice.Parse(item));
                        case "stopped": throw new IOException(L.Get("The audio session ended unexpectedly."));
                        case "paired": return (null, true);
                    }
                    pending = events.Pending;
                    continue;
                }
                promptCancellation.Token.ThrowIfCancellationRequested();
                return (await prompt.ConfigureAwait(false), false);
            }
        }
        finally
        {
            // Close and join a cancellation-aware dialog before publishing a
            // fault or opening the next member. Its cleanup cannot replace the
            // original engine error, EOF, timeout or caller cancellation.
            try { await promptCancellation.CancelAsync().ConfigureAwait(false); } catch { }
            if (prompt is not null) try { await prompt.ConfigureAwait(false); } catch { }
        }
    }
    private sealed class PairingEvents(SessionController controller, IEngineConnection connection, string session, CancellationToken cancellation) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        private Task<JsonElement>? _pending;
        public Task<JsonElement> Pending => _pending ??= controller.ReadEventAsync(connection, session, Timeout.InfiniteTimeSpan, _lifetime.Token);
        public async Task<JsonElement> ReadAsync(TimeSpan wait, CancellationToken token)
        {
            JsonElement item;
            try { item = await Pending.WaitAsync(wait, controller._clock, token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException(L.Get("The audio engine response timed out.")); }
            _pending = null;
            return item;
        }
        public async ValueTask DisposeAsync()
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); } catch { }
            if (_pending is not null) try { await _pending.ConfigureAwait(false); } catch { }
            _lifetime.Dispose();
        }
    }
    private static PlaybackState FailureState(EngineNotice notice) => notice.Code == "preempted" ? PlaybackState.Preempted : PlaybackState.Error;
    private static string FailureMessage(Receiver? receiver, EngineNotice notice) => notice.Code switch
    {
        // `takeover` is the only reason the engine sends (receiver_ownership_guard:
        // the first confirmed takeover yields). `limit` came from the retired
        // reclaim-cycle limiter; no engine with the ownership guard, which the
        // handshake requires, emits it. Its message stays for that reason value.
        "preempted" when notice.Reason == "limit" => L.Get("Another device keeps taking over this AirPlay session. AirFlash yielded and will not reconnect automatically."),
        "preempted" => L.Get("Another device took over this AirPlay session. Reconnect to claim it back."),
        "ownership_unconfirmed" => L.Get("Receiver session ownership could not be confirmed. Automatic recovery is paused. Reconnect manually to continue."),
        _ => GateFailure.Message(receiver, notice) ?? notice.Detail
    };
    private EngineNotice? RecordFailure(long epoch, Exception error)
    {
        // A failure to reach the receiver is desktop evidence, not an engine
        // verdict: keep it distinct from engine protocol errors so the gate
        // classifier does not blame the network for an IPC problem.
        var notice = error switch
        {
            EngineFailure engineFailure => engineFailure.Notice,
            AdaptiveRebuild adaptive => new EngineNotice("adaptive_rebuild", "", "scheduler", adaptive.Message, true),
            EngineShutdownException shutdown => new EngineNotice("engine_cleanup_failed", "", "desktop", shutdown.Message, false),
            SocketException socket => new EngineNotice("network_error", "", "desktop", socket.Message, true),
            NetworkFailure network => new EngineNotice("network_error", "", "desktop", network.Message, true),
            _ => new EngineNotice("host_error", "", "desktop", error.Message)
        };
        PlaybackDiagnostics diagnostics;
        lock (_sync)
        {
            if (epoch != _generation) return null;
            if (notice.Code is "preempted" or "ownership_unconfirmed") _manualRecoveryRequired = true;
            // A failed reconnect must not erase the last streaming failure snapshot.
            diagnostics = _diagnostics = _diagnostics with { LastFault = notice, CaptureBeforeFault = _snapshot.Metrics ?? _diagnostics.CaptureBeforeFault, TransportBeforeFault = _diagnostics.Transport ?? _diagnostics.TransportBeforeFault };
        }
        log?.Invoke($"Session failure: {JsonSerializer.Serialize(diagnostics)}\n{error}");
        return notice;
    }
    private sealed class PairCancelledException : OperationCanceledException;
    private sealed class AdaptiveRebuild(int target, string reason) : IOException(reason)
    {
        public int Target { get; } = target;
    }
    private static bool IsAuthenticationFailure(string message) => new[] { "pairing", "srp", "verification", "credentials", "signature", "identity", "authentication" }.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));
    private void SetConnection(IEngineConnection connection, string session)
    {
        lock (_sync)
        {
            _connection = connection;
            _sessionId = session;
            _startSent = false;
            _sentStandby = null;
            _nowPlayingSupported = false;
            _nowPlayingMayBeSet = false;
            _diagnosticsSupported = false;
            _soundGlowSupported = false;
            _nativeSessionEpoch = null;
            _lastDiagnostic = null;
        }
    }
    private void ClearConnection(IEngineConnection connection)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_connection, connection)) return;
            _connection = null;
            _sessionId = null;
            _startSent = false;
            _sentStandby = null;
            _soundGlowSupported = false;
        }
    }
    private static async Task RequestStopAsync(IEngineConnection connection, string session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await connection.SendAsync(session, "stop", null, timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
    private async Task<JsonElement> ReadEventAsync(IEngineConnection connection, string session, TimeSpan wait, CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(wait, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
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
    private bool MuteRecoveryAvailable(AppSettings settings) => !settings.ShouldMuteEndpoint
        || audio is not IMuteRecoveryService recovery || recovery.Recovery.CanMute;
    private void EnsureMuteReady(AppSettings settings)
    {
        if (!MuteRecoveryAvailable(settings))
            throw new InvalidOperationException(L.Get("Review local audio recovery before starting playback that mutes this PC."));
    }
    private async Task RestoreAudioAsync()
    {
        lock (_sync)
        {
            _transportStreaming = false;
            if (_diagnosticReply is { } pending) pending.Reply.TrySetResult(pending.Fallback);
        }
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
    private void Publish(long epoch, PlaybackState state, string message = "", StreamMetrics? metrics = null, EngineNotice? failure = null, bool isRetrying = false)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (epoch != _generation) return;
            // LastFault survives stops and reconnects for diagnostics. Only the
            // current failure (or a refresh of that same error) owns its hint.
            var hint = state != PlaybackState.Error ? "" : failure is not null
                ? GateFailure.ActionHint(failure) ?? ""
                : _snapshot.Generation == epoch && _snapshot.State == state && _snapshot.Message == message ? _snapshot.ActionHint : "";
            snapshot = new(state, _receiver, message, metrics ?? _snapshot.Metrics, _receiver is null ? 0 : _adaptive.Current > 0 ? _adaptive.Current : _settings.Latency(_receiver), _diagnostics, _receiver is null ? 0 : _snapshot.StreamRate, _deviceVolume, new Dictionary<string, DeviceVolumeState>(_memberVolumes)) { Generation = epoch, Latency = _adaptive.Runtime, PendingLatency = _adaptive.Pending?.Action.Target, ActionHint = hint, IsRetrying = isRetrying };
            _snapshot = snapshot;
        }
        Changed?.Invoke(snapshot);
    }
    private PassiveOperation? TryAdmitPassiveOperation()
    {
        lock (_sync)
        {
            if (_passiveDisposed) return null;
            ++_passiveOperations;
            return new(this);
        }
    }

    private sealed class PassiveOperation(SessionController owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sync)
                if (--owner._passiveOperations == 0) owner._passiveDrained?.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task drained;
        lock (_sync)
        {
            if (_disposeTask is { IsCompleted: false } or { IsCompletedSuccessfully: true }) return new(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _passiveDisposed = true; ++_automaticIntent; _equalizerDisposed = true;
            drained = _passiveOperations == 0 ? Task.CompletedTask
                : (_passiveDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _ = FinishDisposalAsync(completion, drained);
        return new(completion.Task);
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion, Task drained)
    {
        try
        {
            lock (_sync) { _equalizerEdit?.Cancel(); _equalizerEdit = null; _equalizerEditTask = null; }
            // Stop joins the stream worker, including its direct use of the EQ gate
            // at startup. The admission drain joins callers waiting on the writers.
            await StopAsync().ConfigureAwait(false);
            Task? endpointRecovery;
            lock (_sync) endpointRecovery = _endpointRecoveryTask;
            if (endpointRecovery is not null) await endpointRecovery.ConfigureAwait(false);
            await drained.ConfigureAwait(false);
            _equalizerWriter.Dispose();
            _diagnosticWriter.Dispose();
            _nowPlayingWriter.Dispose();
            _soundGlowWriter.Dispose();
            _serial.Dispose();
            _audioWriter.Dispose();
            completion.SetResult();
        }
        catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.SetException(error); }
    }
}
