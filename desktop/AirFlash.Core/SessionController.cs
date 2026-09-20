using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace AirFlash.Core;

public enum PlaybackState { Idle, Connecting, Streaming, Standby, Pairing, Error }
public sealed record StreamMetrics(double? CaptureToSendP95 = null, double? MaxQueueAge = null, long? Underruns = null, long? DroppedFrames = null, int? InputRate = null);
public sealed record SessionSnapshot(PlaybackState State, Receiver? Receiver = null, string Message = "", StreamMetrics? Metrics = null, int TargetLatency = 0, PlaybackDiagnostics? Diagnostics = null, int StreamRate = 0)
{
    public bool IsActive => State is PlaybackState.Connecting or PlaybackState.Streaming or PlaybackState.Standby or PlaybackState.Pairing;
}
public sealed record SessionTiming(TimeSpan Connect, TimeSpan Read, TimeSpan Pin, TimeSpan Retry)
{
    public static SessionTiming Default { get; } = new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1));
}
public sealed class SessionController(IEngineFactory factory, IAudioService audio, Action<string>? log = null, SessionTiming? timing = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _sync = new();
    private readonly SessionTiming _timing = timing ?? SessionTiming.Default;
    private CancellationTokenSource? _lifetime;
    private Task? _work;
    private IEngineConnection? _connection;
    private string? _sessionId;
    private AppSettings _settings = new();
    private Receiver? _receiver;
    private long _generation;
    private bool _muted;
    private PlaybackDiagnostics _diagnostics = new();
    private readonly Dictionary<string, EngineNotice> _warnings = [];
    private SessionSnapshot _snapshot = new(PlaybackState.Idle);
    public SessionSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public event Action<SessionSnapshot>? Changed;
    public bool Muted => _muted;
    private string Signature(Receiver receiver, AppSettings settings) => $"{receiver.TransportKey}|{settings.EffectiveEndpoint}|{settings.Latency(receiver.Id)}|{settings.StreamSampleRate}";
    public async Task StartAsync(Receiver receiver, AppSettings settings)
    {
        if (!receiver.Complete) throw new InvalidOperationException(L.Get("Both stereo pair members must be online."));
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await StartLockedAsync(receiver, settings, null).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    public async Task PairAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>> requestPin)
    {
        if (!receiver.Complete) throw new InvalidOperationException(L.Get("Wait until both stereo pair members are online before pairing."));
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await StartLockedAsync(receiver, settings, requestPin).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    private async Task StartLockedAsync(Receiver receiver, AppSettings settings, Func<Receiver, CancellationToken, Task<string?>>? requestPin)
    {
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
            catch (Exception error) { log?.Invoke(error.ToString()); Publish(epoch, PlaybackState.Error, error.Message); }
            finally { await RestoreAudioAsync().ConfigureAwait(false); }
        }, CancellationToken.None);
    }
    public async Task StopAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await StopLockedAsync().ConfigureAwait(false); _receiver = null; Publish(_generation, PlaybackState.Idle); }
        finally { _serial.Release(); }
    }
    private async Task StopLockedAsync()
    {
        ++_generation;
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
            _settings = settings.Clone();
            if (_receiver is { } receiver && Snapshot.IsActive && Snapshot.State != PlaybackState.Pairing && Signature(receiver, old) != Signature(receiver, settings))
                await StartLockedAsync(receiver, settings, null).ConfigureAwait(false);
            else
            {
                if (!settings.MuteWhileStreaming) await RestoreAudioAsync().ConfigureAwait(false);
                else if (Snapshot.State is PlaybackState.Streaming or PlaybackState.Standby) await audio.MuteAsync(settings.EffectiveEndpoint).ConfigureAwait(false);
                await SendGainAsync().ConfigureAwait(false);
            }
        }
        finally { _serial.Release(); }
    }
    public async Task SetMutedAsync(bool muted)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _muted = muted; await SendGainAsync().ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
    private double Gain(Receiver receiver) => _muted ? 0 : _settings.Gain(receiver.Id);
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
            var startedAt = 0L;
            try
            {
                var settings = _settings;
                var peers = new List<object>();
                foreach (var member in receiver.Peers) peers.Add(new { host = await ResolveAsync(member.Address, cancellation).ConfigureAwait(false), port = member.Port, codecs = member.Codecs.Select(x => (int)x).ToArray() });
                await using var connection = factory.Open();
                var session = Guid.NewGuid().ToString("N");
                SetConnection(connection, session);
                try
                {
                    lock (_sync) { if (epoch == _generation) { _diagnostics = _diagnostics with { Transport = null, Warnings = [] }; _warnings.Clear(); _snapshot = _snapshot with { Metrics = null }; } }
                    Publish(epoch, PlaybackState.Connecting, attempts > 0 ? L.Format("Reconnecting ({0}/{1})", attempts, settings.MaxReconnectAttempts) : L.Get("Connecting…"));
                    var deadline = DateTime.UtcNow + _timing.Connect;
                    await connection.SendAsync(session, "start", new { peers, source = "loopback", duration_ms = 0, latency_ms = settings.Latency(receiver.Id), gain = Gain(receiver), timing = "ptp", capture_endpoint = settings.EffectiveEndpoint, sample_rate = int.Parse(settings.StreamSampleRate) }, cancellation).ConfigureAwait(false);
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var wait = startedAt == 0 ? deadline - DateTime.UtcNow : _timing.Read;
                        if (wait <= TimeSpan.Zero) throw new TimeoutException(L.Get("Timed out connecting to the audio device."));
                        var item = await ReadEventAsync(connection, session, wait, cancellation).ConfigureAwait(false);
                        var kind = item.Text("event");
                        if (kind == "error") throw new EngineFailure(EngineNotice.Parse(item));
                        if (kind == "stopped") throw new IOException(L.Get("The audio session ended unexpectedly."));
                        if (kind == "streaming")
                        {
                            startedAt = Stopwatch.GetTimestamp();
                            if (_settings.MuteWhileStreaming) await audio.MuteAsync(_settings.EffectiveEndpoint).ConfigureAwait(false);
                            Publish(epoch, PlaybackState.Streaming);
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
                        else if (kind == "capture_metrics" && startedAt != 0 && item.TryGetProperty("metrics", out var metrics))
                        {
                            var parsed = new StreamMetrics(metrics.Number("capture_to_send_p95_ms"), metrics.Number("max_queue_age_ms"), metrics.Integer("underrun_packets"), metrics.Integer("dropped_frames"), metrics.Integer("input_rate") is { } rate ? (int)rate : null);
                            var lastAudio = metrics.Number("last_audio_qpc_ns") ?? 0;
                            var silence = lastAudio > 0 ? (Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency) - lastAudio) / 1e9 : Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
                            var threshold = _settings.ReadOptions(receiver.Id).StandbySeconds ?? _settings.StandbySilenceSeconds;
                            Publish(epoch, _settings.StandbyEnabled && silence >= threshold ? PlaybackState.Standby : PlaybackState.Streaming, metrics: parsed);
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
                var notice = error is EngineFailure engineFailure ? engineFailure.Notice : new EngineNotice("host_error", "", "desktop", error.Message);
                lock (_sync)
                {
                    if (epoch != _generation) return;
                    // A failed reconnect must not erase the last streaming failure snapshot.
                    _diagnostics = _diagnostics with { LastFault = notice, CaptureBeforeFault = _snapshot.Metrics ?? _diagnostics.CaptureBeforeFault, TransportBeforeFault = _diagnostics.Transport ?? _diagnostics.TransportBeforeFault };
                }
                log?.Invoke($"Session failure: {JsonSerializer.Serialize(_diagnostics)}\n{error}");
                Publish(epoch, PlaybackState.Error, notice.Detail);
                if (!_settings.ForceReconnect || attempts > _settings.MaxReconnectAttempts || notice.Retryable == false || notice.Retryable is null && IsAuthenticationFailure(error.Message)) return;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(_timing.Retry.TotalMilliseconds * Math.Pow(2, attempts - 1), 8000));
            Publish(epoch, PlaybackState.Connecting, L.Format("Disconnected; reconnecting ({0}/{1})", attempts, _settings.MaxReconnectAttempts));
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            lock (_sync) { if (epoch == _generation) _diagnostics = _diagnostics with { ReconnectCount = _diagnostics.ReconnectCount + 1 }; }
        }
    }
    private async Task PairMembersAsync(Receiver receiver, Func<Receiver, CancellationToken, Task<string?>> requestPin, long epoch, CancellationToken cancellation)
    {
        foreach (var member in receiver.Peers)
        {
            await using var connection = factory.Open();
            var session = Guid.NewGuid().ToString("N");
            SetConnection(connection, session);
            try
            {
                Publish(epoch, PlaybackState.Pairing, L.Format("Pairing {0} ({1})", member.Name, member.Address));
                await connection.SendAsync(session, "pair", new { peer = new { host = await ResolveAsync(member.Address, cancellation).ConfigureAwait(false), port = member.Port } }, cancellation).ConfigureAwait(false);
                while (true)
                {
                    var item = await ReadEventAsync(connection, session, _timing.Connect, cancellation).ConfigureAwait(false);
                    switch (item.Text("event"))
                    {
                        case "pin_required":
                            using (var pinTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                            {
                                pinTimeout.CancelAfter(_timing.Pin);
                                var pin = await requestPin(member, pinTimeout.Token).ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(pin)) { Publish(epoch, PlaybackState.Idle, L.Get("Pairing cancelled.")); throw new PairCancelledException(); }
                                await connection.SendAsync(session, "pair_pin", new { pin }, cancellation).ConfigureAwait(false);
                            }
                            break;
                        case "paired": goto Paired;
                        case "error": throw new IOException(item.Text("message", L.Get("Pairing failed.")));
                    }
                }
            Paired:;
            }
            finally { ClearConnection(connection); await RequestStopAsync(connection, session).ConfigureAwait(false); }
        }
    }
    private sealed class EngineFailure(EngineNotice notice) : IOException(notice.Detail) { public EngineNotice Notice { get; } = notice; }
    private sealed class PairCancelledException : OperationCanceledException;
    private static bool IsAuthenticationFailure(string message) => new[] { "pairing", "srp", "verification", "credentials", "signature", "identity", "authentication" }.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));
    private void SetConnection(IEngineConnection connection, string session) { lock (_sync) { _connection = connection; _sessionId = session; } }
    private void ClearConnection(IEngineConnection connection) { lock (_sync) if (ReferenceEquals(_connection, connection)) { _connection = null; _sessionId = null; } }
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
                var item = await connection.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException(L.Get("The audio engine exited."));
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
        try { await audio.RestoreAsync().ConfigureAwait(false); } catch (Exception error) { log?.Invoke(L.Format("Failed to restore local mute state: {0}", error.Message)); }
    }
    private void Publish(long epoch, PlaybackState state, string message = "", StreamMetrics? metrics = null)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (epoch != _generation) return;
            snapshot = new(state, _receiver, message, metrics ?? _snapshot.Metrics, _receiver is null ? 0 : _settings.Latency(_receiver.Id), _diagnostics, int.TryParse(_settings.StreamSampleRate, out var streamRate) ? streamRate : 0);
            _snapshot = snapshot;
        }
        Changed?.Invoke(snapshot);
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _serial.Dispose(); }
}
