using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SessionTests
{
    private static Receiver Pod(string id = "a") => new(id, id, "127.0.0.1");
    private static AppSettings Settings() => new() { AutoConnectOnDiscover = false, ForceReconnect = false, MasterVolume = 10 };
    private static readonly SessionTiming Fast = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
    [Fact]
    public async Task StartIsNonblockingAndStopCancelsPendingConnection()
    {
        var process = new FakeConnection(false); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()).WaitAsync(TimeSpan.FromSeconds(1)); await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Connecting, controller.Snapshot.State); await controller.StopAsync(); Assert.True(process.Disposed); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }
    [Fact]
    public async Task GainPrecedesAudioAndUsesReceiverTimesMaster()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.Options("a").Volume = 25; await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var command = process.Commands.Single(c => c.Command == "start"); Assert.Equal(.025, command.Parameters.Number("gain")); Assert.Equal(200, command.Parameters.Number("latency_ms"));
        Assert.Equal(44100, command.Parameters.Number("sample_rate"));
        Assert.Equal(JsonValueKind.Array, command.Parameters.GetProperty("peers")[0].GetProperty("codecs").ValueKind);
    }
    [Fact]
    public async Task ChangingStreamRateRestartsSessionWithNewRate()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.StreamSampleRate = "48000";
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(48000, first.Commands.Single(c => c.Command == "start").Parameters.Number("sample_rate"));
        Assert.Equal(48000, controller.Snapshot.StreamRate);
        settings.StreamSampleRate = "44100"; await controller.UpdateSettingsAsync(settings); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, factory.OpenCount); Assert.Equal(44100, second.Commands.Single(c => c.Command == "start").Parameters.Number("sample_rate"));
    }
    [Fact]
    public async Task SwitchingWaitsForPreviousCleanupAndRejectsStaleEvents()
    {
        var first = new FakeConnection(); var second = new FakeConnection(false); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        await controller.StartAsync(Pod("b"), Settings()); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(first.Disposed);
        second.Emit("error", session: first.Session, message: "stale"); second.Emit("streaming"); await Until(() => controller.Snapshot.State == PlaybackState.Streaming); Assert.Equal("b", controller.Snapshot.Receiver!.Id);
    }
    [Fact]
    public async Task SwitchDuringFailureWaitsForDispose()
    {
        var first = new FakeConnection { BlockDispose = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var second = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        first.Emit("error", message: "peer disconnected"); await first.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var change = controller.StartAsync(Pod("b"), Settings()); Assert.False(second.Started.Task.IsCompleted); first.BlockDispose.SetResult(); await change; await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(first.Disposed);
    }
    [Fact]
    public async Task ReconnectCleansOldProcessAndUsesFreshSession()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(first, second), audio, timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true; settings.MaxReconnectAttempts = 1;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming); first.Emit("error", message: "network lost");
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(first.Disposed); Assert.NotEqual(first.Session, second.Session);
    }
    [Fact]
    public async Task AuthenticationFailureDoesNotReconnect()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true; await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.Emit("error", message: "authentication signature verification failed"); await Until(() => process.Disposed); Assert.Equal(1, factory.OpenCount); Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
    }
    [Fact]
    public async Task ConnectionTimeoutBecomesActionableError()
    {
        var process = new FakeConnection(false); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast with { Connect = TimeSpan.FromMilliseconds(60) });
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Error); Assert.Contains("timed out", controller.Snapshot.Message); await Until(() => process.Disposed);
    }
    [Fact]
    public async Task ApplyingEndpointAndLatencyRestartsOnceVolumeDoesNotRestart()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.CaptureMode = "endpoint"; settings.CaptureEndpoint = "device-id"; settings.LatencyMode = "buffered"; await controller.UpdateSettingsAsync(settings); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, factory.OpenCount); Assert.Equal("device-id", second.Commands.Single(c => c.Command == "start").Parameters.Text("capture_endpoint"));
        settings.MasterVolume = 20; await controller.UpdateSettingsAsync(settings); Assert.Equal(2, factory.OpenCount); Assert.Contains(second.Commands, c => c.Command == "set_gain" && c.Parameters.Number("gain") == .2);
    }
    [Fact]
    public async Task StopAndFailedSessionRestoreMute()
    {
        var process = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(process), audio, timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => audio.Muted); process.Emit("error", message: "peer closed"); await Until(() => process.Disposed); Assert.False(audio.Muted);
    }
    [Fact]
    public async Task TurningOffMuteWhileStreamingRestoresImmediately()
    {
        var process = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(process), audio, timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => audio.Muted); settings.MuteWhileStreaming = false; await controller.UpdateSettingsAsync(settings); Assert.False(audio.Muted);
    }
    [Fact]
    public async Task SilentMetricsEnterStandbyWithoutRestart()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true; settings.StandbySilenceSeconds = 5;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.Emit("capture_metrics", metrics: new { last_audio_qpc_ns = 1, capture_to_send_p95_ms = (double?)null, underrun_packets = 2 });
        await Until(() => controller.Snapshot.State == PlaybackState.Standby); Assert.Equal(1, factory.OpenCount); Assert.Null(controller.Snapshot.Metrics!.CaptureToSendP95);
    }
    [Fact]
    public async Task IncompleteStereoGroupNeverStartsEngine()
    {
        var factory = new FakeFactory(); await using var controller = new SessionController(factory, new FakeAudio());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Pod() with { StereoId = "pair", Members = [Pod()] }, Settings())); Assert.Equal(0, factory.OpenCount);
    }
    [Fact]
    public async Task PairCancellationDoesNotStartPlayback()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.PairAsync(Pod(), Settings(), (_, _) => Task.FromResult<string?>(null)); await Until(() => process.Disposed);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.DoesNotContain(process.Commands, c => c.Command == "start");
    }
    [Fact]
    public async Task PairBothMembersThenStartOneStereoSession()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var stream = new FakeConnection(); var factory = new FakeFactory(first, second, stream); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var group = Pod() with { StereoId = "pair", Members = [Pod(), Pod("b") with { Address = "127.0.0.2" }] };
        await controller.PairAsync(group, Settings(), (_, _) => Task.FromResult<string?>("1234")); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(3, factory.OpenCount); Assert.True(first.Disposed); Assert.True(second.Disposed); Assert.Equal(2, stream.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers").GetArrayLength());
    }
    [Fact]
    public async Task RecoverableWarningsAndTransportMetricsDoNotRestartOrUnmute()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var audio = new FakeAudio();
        await using var controller = new SessionController(factory, audio, timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.Emit("capture_metrics", metrics: new { underrun_packets = 2, input_rate = 48000 });
        process.EmitData(new { @event = "warning", code = "feedback_delayed", host = "a", channel = "feedback", message = "slow", recovered = false });
        process.EmitData(new { @event = "warning", code = "media_send_delayed", host = "b", channel = "media", message = "busy", recovered = false });
        process.EmitData(new { @event = "transport_metrics", session_uptime_ms = 1234, sender_late_recoveries = 3, members = new object[] { new { host = "a", media = new { retransmits_sent = 4 }, health = new { feedback_delayed = true, feedback_rtt_ms = 42 } }, new { host = "b" } } });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null);
        Assert.Equal(1, factory.OpenCount); Assert.True(audio.Muted); Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
        Assert.Equal(2, controller.Snapshot.Metrics!.Underruns); Assert.Null(controller.Snapshot.Metrics.DroppedFrames);
        Assert.Equal(2, controller.Snapshot.Diagnostics!.Warnings!.Count);
        var transport = controller.Snapshot.Diagnostics.Transport!; Assert.Equal(3, transport.SenderLateRecoveries); Assert.Equal(4, transport.Members[0].RetransmitsSent); Assert.Null(transport.Members[1].RetransmitsSent);
        process.EmitData(new { @event = "warning", code = "feedback_recovered", host = "a", channel = "feedback", message = "ok", recovered = true });
        await Until(() => controller.Snapshot.Diagnostics?.Warnings?.Count == 1);
        Assert.Equal("b", controller.Snapshot.Diagnostics!.Warnings![0].Host); Assert.True(audio.Muted);
    }
    [Fact]
    public async Task ReconnectRetainsFailureAndMetricsUntilANewPlayback()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var third = new FakeConnection(); var factory = new FakeFactory(first, second, third);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        first.Emit("capture_metrics", metrics: new { underrun_packets = 7, dropped_frames = 11 });
        first.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 2, members = new[] { new { host = "a", media = new { retransmits_sent = 5 } } } });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null);
        first.EmitData(new { @event = "error", code = "peer_closed", host = "a", channel = "events", message = "connection ended", retryable = true });
        await Until(() => factory.OpenCount == 2 && controller.Snapshot.State == PlaybackState.Streaming);
        var diagnostics = controller.Snapshot.Diagnostics!;
        Assert.Equal(1, diagnostics.ReconnectCount); Assert.Equal("peer_closed", diagnostics.LastFault!.Code);
        Assert.Equal(7, diagnostics.CaptureBeforeFault!.Underruns); Assert.Equal(2, diagnostics.TransportBeforeFault!.SenderLateRecoveries);
        Assert.Null(controller.Snapshot.Metrics); Assert.Null(diagnostics.Transport);
        second.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 0, members = Array.Empty<object>() });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null); Assert.Equal(2, controller.Snapshot.Diagnostics!.TransportBeforeFault!.SenderLateRecoveries);
        await controller.StartAsync(Pod("new"), settings); await Until(() => factory.OpenCount == 3 && controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(0, controller.Snapshot.Diagnostics!.ReconnectCount); Assert.Null(controller.Snapshot.Diagnostics.LastFault);
    }
    [Fact]
    public async Task StructuredNonretryableFaultDoesNotDependOnMessageText()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.EmitData(new { @event = "error", code = "protocol_error", host = "a", channel = "feedback", message = "invalid reply", retryable = false });
        await Until(() => process.Disposed && controller.Snapshot.State == PlaybackState.Error);
        Assert.Equal(1, factory.OpenCount); Assert.Equal("feedback", controller.Snapshot.Diagnostics!.LastFault!.Channel);
    }
    [Fact]
    public async Task StaleTransportAndWarningsCannotModifyNewPlayback()
    {
        var first = new FakeConnection(); var second = new FakeConnection();
        await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        await controller.StartAsync(Pod("new"), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        second.EmitData(new { @event = "warning", host = "old", channel = "events", message = "stale" }, first.Session);
        second.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 99 }, first.Session);
        second.Emit("capture_metrics", metrics: new { underrun_packets = 1 });
        await Until(() => controller.Snapshot.Metrics?.Underruns == 1);
        Assert.Null(controller.Snapshot.Diagnostics!.Transport); Assert.Empty(controller.Snapshot.Diagnostics.Warnings ?? []);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class FakeFactory(params FakeConnection[] processes) : IEngineFactory
    {
        private readonly ConcurrentQueue<FakeConnection> _processes = new(processes);
        public int OpenCount;
        public IEngineConnection Open() { Interlocked.Increment(ref OpenCount); return _processes.TryDequeue(out var p) ? p : throw new IOException("unexpected engine spawn"); }
    }
    private sealed record EngineCommand(string Session, string CommandName, JsonElement Parameters) { public string Command => CommandName; }
    private sealed class FakeConnection(bool autoReady = true) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<EngineCommand> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? BlockDispose { get; init; }
        public string Session { get; private set; } = "";
        public bool Disposed { get; private set; }
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            Commands.Enqueue(new(session, command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            switch (command)
            {
                case "start": Session = session; Started.TrySetResult(); if (autoReady) Emit("streaming"); break;
                case "pair": Session = session; Emit("pin_required"); break;
                case "pair_pin": Emit("paired"); break;
                case "stop": _events.Writer.TryComplete(); break;
            }
            return Task.CompletedTask;
        }
        public void EmitData(object payload, string? session = null)
        {
            var data = JsonSerializer.SerializeToNode(payload)!.AsObject(); data["version"] = 1; data["session_id"] = session ?? Session;
            _events.Writer.TryWrite(JsonSerializer.SerializeToElement(data));
        }
        public void Emit(string kind, string? session = null, string? message = null, object? metrics = null) => _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session ?? Session, @event = kind, message, metrics }));
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) { try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); if (BlockDispose is not null) await BlockDispose.Task; Disposed = true; _events.Writer.TryComplete(); }
    }
    private sealed class FakeAudio : IAudioService
    {
        public bool Muted;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("default");
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId) { Muted = true; return Task.CompletedTask; }
        public Task RestoreAsync() { Muted = false; return Task.CompletedTask; }
    }
}
