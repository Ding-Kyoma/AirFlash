using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class CapturePaddingMetricsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly SessionTiming Timing = new(Deadline, Deadline, Deadline, TimeSpan.Zero);

    [Fact]
    public void ExistingFiveParameterConstructionLeavesNewCountersUnavailable()
    {
        var metrics = new StreamMetrics(2.5, 8.5, 3, 4, 48000);
        Assert.Equal(2.5, metrics.CaptureToSendP95);
        Assert.Equal(8.5, metrics.MaxQueueAge);
        Assert.Equal(3, metrics.Underruns);
        Assert.Equal(4, metrics.DroppedFrames);
        Assert.Equal(48000, metrics.InputRate);
        Assert.Null(metrics.PaddedFrames);
        Assert.Null(metrics.FullyPaddedPackets);
    }

    [Fact]
    public void ExistingEightParameterConstructionAndDeconstructionKeepTheOriginalCounterOrder()
    {
        var metrics = new StreamMetrics(2.5, 8.5, 3, 4, 48000, 5, 6, 7);
        var (p95, queueAge, underruns, dropped, rate, rebinds, probes, storms) = metrics;
        (double?, double?, long?, long?, int?, long?, long?, long?) expected = (2.5, 8.5, 3, 4, 48000, 5, 6, 7);
        Assert.Equal(expected, (p95, queueAge, underruns, dropped, rate, rebinds, probes, storms));
        Assert.Null(metrics.PaddedFrames);
        Assert.Null(metrics.FullyPaddedPackets);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task NewOptionalCaptureCountersReachLiveSnapshot(int rate)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings(rate));
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);

        connection.Capture(new { capture_to_send_p95_ms = 2.5, max_queue_age_ms = 8.5,
            underrun_packets = 7, dropped_frames = 4, input_rate = rate, rebinds = 2, silent_probes = 3, rebind_storms = 4,
            padded_frames = 353UL, fully_padded_packets = 1UL });
        var snapshot = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 7);
        Assert.Equal(PlaybackState.Streaming, snapshot.State);
        Assert.Equal(rate, snapshot.StreamRate);
        Assert.Equal(2.5, snapshot.Metrics!.CaptureToSendP95);
        Assert.Equal(8.5, snapshot.Metrics.MaxQueueAge);
        Assert.Equal(4, snapshot.Metrics.DroppedFrames);
        Assert.Equal(rate, snapshot.Metrics.InputRate);
        Assert.Equal(2, snapshot.Metrics.Rebinds);
        Assert.Equal(3, snapshot.Metrics.SilentProbes);
        Assert.Equal(4, snapshot.Metrics.RebindStorms);
        Assert.Equal(353UL, snapshot.Metrics.PaddedFrames);
        Assert.Equal(1UL, snapshot.Metrics.FullyPaddedPackets);
        Assert.Same(snapshot.Metrics, controller.Snapshot.Metrics);
        Assert.Equal(1, factory.OpenCount);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public async Task UnsignedCounterBoundariesArePreservedWithoutSignedTruncation(ulong value)
    {
        var connection = new Connection();
        await using var controller = TestSessionController.Create(new Factory(connection), new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings());
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        connection.Capture(new { underrun_packets = 1, padded_frames = value, fully_padded_packets = value });
        var snapshot = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 1);
        Assert.Equal(value, snapshot.Metrics!.PaddedFrames);
        Assert.Equal(value, snapshot.Metrics.FullyPaddedPackets);
    }

    public static IEnumerable<object?[]> InvalidOptionalCounters()
    {
        foreach (var field in new[] { "padded_frames", "fully_padded_packets" })
            foreach (var token in new string?[] { null, "null", "-1", "1.5", "\"12\"",
                "18446744073709551616", "true", "[]", "{}" })
                yield return [field, token];
    }

    [Fact]
    public async Task CustomPlaybackHistorySurvivesStandbyAndRetryWithoutReplayingCounters()
    {
        var first = new Connection(autoStreaming: false);
        var second = new Connection(autoStreaming: false);
        var logs = new ConcurrentQueue<string>();
        await using var controller = TestSessionController.Create(new Factory(first, second), new Audio(), logs.Enqueue, Timing);
        using var snapshots = new Snapshots(controller);
        var settings = Settings(reconnect: true);
        settings.LatencyMode = "custom";
        await controller.StartAsync(Receiver(), settings);

        await first.Started.Task.WaitAsync(Deadline);
        first.Emit(new { @event = "streaming", session_epoch = 1 });
        await snapshots.NextAsync(s => s.State == PlaybackState.Streaming);
        var logCount = logs.Count;
        first.Capture(new { padded_frames = 0 });
        first.Capture(new { padded_frames = 12 });
        first.Emit(new { @event = "transport_metrics", session_epoch = 1, skipped_packets = 0 });
        first.Emit(new { @event = "transport_metrics", session_epoch = 1, skipped_packets = 2 });
        var initial = await snapshots.NextAsync(s => s.Diagnostics?.Transport?.SkippedPackets == 2);
        Assert.Equal(2, initial.Diagnostics!.CounterDeltaHistory!.Count);
        Assert.Equal(logCount, logs.Count);

        first.Emit(new { @event = "standby" });
        first.Capture(new { padded_frames = 400 });
        first.Emit(new { @event = "resuming" });
        first.Emit(new { @event = "streaming", session_epoch = 2 });
        first.Capture(new { padded_frames = 800 });
        var resumed = await snapshots.NextAsync(s => s.Metrics?.PaddedFrames == 800);
        Assert.Same(initial.Diagnostics.CounterDeltaHistory, resumed.Diagnostics!.CounterDeltaHistory);
        first.Fault(retryable: true);

        await second.Started.Task.WaitAsync(Deadline);
        second.Emit(new { @event = "streaming", session_epoch = 1 });
        second.Capture(new { padded_frames = 900 }, first.Session);
        second.Capture(new { padded_frames = 0 });
        second.Capture(new { padded_frames = 20 });
        var current = await snapshots.NextAsync(s => s.Metrics?.PaddedFrames == 20);
        var history = current.Diagnostics!.CounterDeltaHistory!;
        Assert.Equal(3, history.Count);
        Assert.Equal([1, 1, 2], history.Select(delta => delta.Attempt));
        Assert.Equal(20UL, history[^1].Counters!["padded_frames"]);
        var copied = JsonSerializer.SerializeToElement(current.Diagnostics, AppSettings.JsonOptions);
        Assert.Equal(3, copied.GetProperty("counter_delta_history").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(InvalidOptionalCounters))]
    public async Task InvalidOrAbsentCounterIsUnavailableWithoutErasingOtherMetrics(string field, string? token)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings());
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        var other = field == "padded_frames" ? "fully_padded_packets" : "padded_frames";
        var metrics = new JsonObject { [other] = 9UL, ["underrun_packets"] = 3,
            ["dropped_frames"] = 5, ["input_rate"] = 44100, ["max_queue_age_ms"] = 4.5 };
        if (token is not null) metrics[field] = JsonNode.Parse(token);
        connection.Capture(metrics);
        var snapshot = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 3);
        Assert.Equal(PlaybackState.Streaming, snapshot.State);
        Assert.Equal(5, snapshot.Metrics!.DroppedFrames);
        Assert.Equal(44100, snapshot.Metrics.InputRate);
        Assert.Equal(4.5, snapshot.Metrics.MaxQueueAge);
        if (field == "padded_frames")
        {
            Assert.Null(snapshot.Metrics.PaddedFrames);
            Assert.Equal(9UL, snapshot.Metrics.FullyPaddedPackets);
        }
        else
        {
            Assert.Equal(9UL, snapshot.Metrics.PaddedFrames);
            Assert.Null(snapshot.Metrics.FullyPaddedPackets);
        }
        Assert.Equal(1, factory.OpenCount);
        Assert.Null(snapshot.Diagnostics!.LastFault);
    }

    [Fact]
    public async Task OldProducerEventClearsPreviouslyAvailableOptionalValues()
    {
        var connection = new Connection();
        await using var controller = TestSessionController.Create(new Factory(connection), new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings());
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        connection.Capture(new { underrun_packets = 1, padded_frames = 352UL, fully_padded_packets = 1UL });
        var before = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 1);
        Assert.Equal(352UL, before.Metrics!.PaddedFrames);
        connection.Capture(new { capture_to_send_p95_ms = 1.25, max_queue_age_ms = 7.5,
            underrun_packets = 2, dropped_frames = 8, input_rate = 48000 });
        var after = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 2);
        Assert.Equal(1.25, after.Metrics!.CaptureToSendP95);
        Assert.Equal(7.5, after.Metrics.MaxQueueAge);
        Assert.Equal(8, after.Metrics.DroppedFrames);
        Assert.Equal(48000, after.Metrics.InputRate);
        Assert.Null(after.Metrics.PaddedFrames);
        Assert.Null(after.Metrics.FullyPaddedPackets);
        Assert.Equal(352UL, before.Metrics.PaddedFrames);
        Assert.Equal(1UL, before.Metrics.FullyPaddedPackets);
    }

    [Theory]
    [InlineData("connecting")]
    [InlineData("standby")]
    [InlineData("resuming")]
    public async Task CaptureMetricsBeforeOrBetweenStreamingKeepNativeLifecycleState(string phase)
    {
        var connection = new Connection(autoStreaming: false);
        await using var controller = TestSessionController.Create(new Factory(connection), new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings());
        await connection.Started.Task.WaitAsync(Deadline);
        if (phase != "connecting") connection.Emit(new { @event = "standby", reason = "application_unavailable" });
        if (phase == "resuming") connection.Emit(new { @event = "resuming" });
        connection.Capture(new { underrun_packets = 99, padded_frames = 999UL, fully_padded_packets = 9UL });
        var current = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 99);
        Assert.Equal(phase == "standby" ? PlaybackState.Standby : PlaybackState.Connecting, current.State);
        Assert.Equal(999UL, current.Metrics!.PaddedFrames);
        Assert.Equal(9UL, current.Metrics.FullyPaddedPackets);
        connection.Emit(new { @event = "streaming" });
        var streaming = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        Assert.Same(current.Metrics, streaming.Metrics);
    }

    [Fact]
    public async Task ReplacedApplicationCaptureWorkerCanResetCountersWithinOneEngineSession()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        var settings = Settings(); settings.CaptureMode = "application"; settings.CaptureApplicationPath = @"C:\Synthetic\Player.exe";
        await controller.StartAsync(Receiver(), settings);
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        connection.Capture(new { underrun_packets = 3, padded_frames = 705UL, fully_padded_packets = 2UL });
        var prior = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 3);
        var session = connection.Session;
        connection.Emit(new { @event = "standby", reason = "application_unavailable" });
        connection.Capture(new { underrun_packets = 0, input_rate = 0 });
        var absent = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Standby && snapshot.Metrics?.Underruns == 0);
        Assert.Null(absent.Metrics!.PaddedFrames); Assert.Null(absent.Metrics.FullyPaddedPackets);
        connection.Emit(new { @event = "resuming" }); connection.Emit(new { @event = "streaming" });
        connection.Capture(new { underrun_packets = 0, padded_frames = 0UL, fully_padded_packets = 0UL, input_rate = 48000 });
        var fresh = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Metrics?.PaddedFrames == 0);
        Assert.Equal(0UL, fresh.Metrics!.FullyPaddedPackets);
        connection.Capture(new { underrun_packets = 1, padded_frames = 1UL, fully_padded_packets = 0UL, input_rate = 48000 });
        var replaced = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Metrics?.Underruns == 1);
        Assert.Equal(1UL, replaced.Metrics!.PaddedFrames); Assert.Equal(0UL, replaced.Metrics.FullyPaddedPackets);
        Assert.Equal(705UL, prior.Metrics!.PaddedFrames); Assert.Equal(2UL, prior.Metrics.FullyPaddedPackets);
        Assert.Equal(session, connection.Session); Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task StaleSessionAndWrongVersionCaptureEventsCannotModifyReplacement()
    {
        var first = new Connection(); var second = new Connection();
        await using var controller = TestSessionController.Create(new Factory(first, second), new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings());
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        var oldSession = first.Session;
        await controller.StartAsync(Receiver("replacement"), Settings());
        var replacement = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == "replacement");
        Assert.Null(replacement.Metrics);
        Assert.NotEqual(oldSession, second.Session);
        second.Capture(new { underrun_packets = 98, padded_frames = 980UL, fully_padded_packets = 98UL }, oldSession);
        second.Capture(new { underrun_packets = 99, padded_frames = 990UL, fully_padded_packets = 99UL }, version: 2);
        second.Capture(new { underrun_packets = 2 });
        var current = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 2);
        Assert.Null(current.Metrics!.PaddedFrames);
        Assert.Null(current.Metrics.FullyPaddedPackets);
        Assert.Null(current.Diagnostics!.CaptureBeforeFault);
        Assert.Null(current.Diagnostics.LastFault);
        Assert.DoesNotContain(snapshots.Published, snapshot => snapshot.Metrics?.Underruns is 98 or 99);
    }

    [Fact]
    public async Task RetryResetsLiveCountersAndRetainsImmutablePreFaultSnapshot()
    {
        var first = new Connection(); var gate = new Gate();
        var second = new Connection(startGate: gate); var third = new Connection();
        var factory = new Factory(first, second, third);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings(reconnect: true));
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        first.Capture(new { underrun_packets = 3, padded_frames = 705UL, fully_padded_packets = 2UL });
        var original = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 3);
        first.Fault(retryable: true);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            var retry = controller.Snapshot;
            Assert.Equal(PlaybackState.Connecting, retry.State);
            Assert.Null(retry.Metrics);
            Assert.Equal(1, retry.Diagnostics!.ReconnectCount);
            Assert.Same(original.Metrics, retry.Diagnostics.CaptureBeforeFault);
            Assert.Equal(705UL, retry.Diagnostics.CaptureBeforeFault!.PaddedFrames);
            Assert.Equal(2UL, retry.Diagnostics.CaptureBeforeFault.FullyPaddedPackets);
            Assert.NotEqual(first.Session, second.Session);
            gate.Release.TrySetResult();
            var restarted = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Diagnostics?.ReconnectCount == 1);
            Assert.Null(restarted.Metrics);
            second.Capture(new { underrun_packets = 1, padded_frames = 1UL, fully_padded_packets = 0UL });
            var latest = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 1);
            Assert.Equal(1UL, latest.Metrics!.PaddedFrames);
            Assert.Equal(0UL, latest.Metrics.FullyPaddedPackets);
            Assert.Same(original.Metrics, latest.Diagnostics!.CaptureBeforeFault);
            Assert.Equal(705UL, original.Metrics!.PaddedFrames);

            second.Fault(retryable: false);
            var terminal = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Error && snapshot.Diagnostics?.ReconnectCount == 1);
            Assert.Same(latest.Metrics, terminal.Diagnostics!.CaptureBeforeFault);
            Assert.Equal(1UL, terminal.Diagnostics.CaptureBeforeFault!.PaddedFrames);
            Assert.Equal(0UL, terminal.Diagnostics.CaptureBeforeFault.FullyPaddedPackets);
            Assert.Same(original.Metrics, retry.Diagnostics.CaptureBeforeFault);
            Assert.Equal(705UL, retry.Diagnostics.CaptureBeforeFault!.PaddedFrames);
            Assert.Equal(2, factory.OpenCount);

            await controller.StartAsync(Receiver("fresh-play"), Settings());
            var fresh = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == "fresh-play");
            Assert.Null(fresh.Metrics);
            Assert.Null(fresh.Diagnostics!.CaptureBeforeFault);
            Assert.Null(fresh.Diagnostics.LastFault);
            Assert.Equal(0, fresh.Diagnostics.ReconnectCount);
            Assert.Equal(3, factory.OpenCount);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task RetryFailingBeforeNewCaptureRetainsLastStreamingCounters()
    {
        var first = new Connection(); var second = new Connection(autoStreaming: false);
        var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings(reconnect: true));
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        first.Capture(new { underrun_packets = 4, padded_frames = ulong.MaxValue, fully_padded_packets = 4UL });
        var original = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 4);
        first.Fault(retryable: true);
        await second.Started.Task.WaitAsync(Deadline);
        Assert.Null(controller.Snapshot.Metrics);
        second.Fault(retryable: false);
        var terminal = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Error && snapshot.Diagnostics?.ReconnectCount == 1);
        Assert.Null(terminal.Metrics);
        Assert.Same(original.Metrics, terminal.Diagnostics!.CaptureBeforeFault);
        Assert.Equal(ulong.MaxValue, terminal.Diagnostics.CaptureBeforeFault!.PaddedFrames);
        Assert.Equal(4UL, terminal.Diagnostics.CaptureBeforeFault.FullyPaddedPackets);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task StopCancelsHeldRetryWithoutPublishingItsCaptureOrStartingAnotherWorker()
    {
        var first = new Connection(); var gate = new Gate(); var second = new Connection(startGate: gate);
        var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings(reconnect: true));
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        first.Capture(new { underrun_packets = 2, padded_frames = 353UL, fully_padded_packets = 1UL });
        var original = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 2);
        first.Fault(retryable: true);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            await controller.StopAsync().WaitAsync(Deadline);
            var stopped = controller.Snapshot;
            Assert.Equal(PlaybackState.Idle, stopped.State);
            Assert.Null(stopped.Metrics);
            Assert.Same(original.Metrics, stopped.Diagnostics!.CaptureBeforeFault);
            Assert.Equal(353UL, stopped.Diagnostics.CaptureBeforeFault!.PaddedFrames);
            Assert.True(second.Disposed.Task.IsCompletedSuccessfully);
            Assert.Equal(2, factory.OpenCount);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalFaultCapturesAvailableOrUnavailableCounters(bool newProducer)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        using var snapshots = new Snapshots(controller);
        await controller.StartAsync(Receiver(), Settings(reconnect: true));
        await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Streaming);
        var metrics = new JsonObject { ["underrun_packets"] = 5 };
        if (newProducer) { metrics["padded_frames"] = 1408UL; metrics["fully_padded_packets"] = 4UL; }
        connection.Capture(metrics);
        var before = await snapshots.NextAsync(snapshot => snapshot.Metrics?.Underruns == 5);
        connection.Fault(retryable: false);
        var failed = await snapshots.NextAsync(snapshot => snapshot.State == PlaybackState.Error);
        Assert.Same(before.Metrics, failed.Diagnostics!.CaptureBeforeFault);
        Assert.Equal(newProducer ? 1408UL : (ulong?)null, failed.Diagnostics.CaptureBeforeFault!.PaddedFrames);
        Assert.Equal(newProducer ? 4UL : (ulong?)null, failed.Diagnostics.CaptureBeforeFault.FullyPaddedPackets);
        Assert.Equal("synthetic_capture_fault", failed.Diagnostics.LastFault!.Code);
        Assert.False(failed.Diagnostics.LastFault.Retryable);
        Assert.Equal(1, factory.OpenCount);
        Assert.True(connection.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, audio.MuteCalls);
    }

    private static Receiver Receiver(string id = "synthetic-capture") => new(id, "Synthetic receiver", "198.51.100.10");
    private static AppSettings Settings(int rate = 48000, bool reconnect = false)
    {
        var settings = new AppSettings { ForceReconnect = reconnect, MaxReconnectAttempts = 2,
            StandbyEnabled = false, AutoConnectOnDiscover = false, MuteWhileStreaming = false };
        settings.Options("synthetic-capture").SampleRate = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return settings;
    }

    private sealed class Snapshots : IDisposable
    {
        private readonly SessionController _controller;
        private readonly Channel<SessionSnapshot> _snapshots = Channel.CreateUnbounded<SessionSnapshot>();
        public ConcurrentQueue<SessionSnapshot> Published { get; } = new();
        public Snapshots(SessionController controller) { _controller = controller; _controller.Changed += Changed; }
        private void Changed(SessionSnapshot snapshot) { Published.Enqueue(snapshot); _snapshots.Writer.TryWrite(snapshot); }
        public async Task<SessionSnapshot> NextAsync(Func<SessionSnapshot, bool> condition)
        {
            using var timeout = new CancellationTokenSource(Deadline);
            while (true)
            {
                var snapshot = await _snapshots.Reader.ReadAsync(timeout.Token);
                if (condition(snapshot)) return snapshot;
            }
        }
        public void Dispose() { _controller.Changed -= Changed; _snapshots.Writer.TryComplete(); }
    }

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public async Task WaitAsync(CancellationToken cancellation) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellation); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _connections = new(connections);
        private int _openCount;
        public int OpenCount => Volatile.Read(ref _openCount);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref _openCount);
            return _connections.TryDequeue(out var connection) ? connection : throw new InvalidOperationException("Unexpected synthetic engine attempt.");
        }
    }

    private sealed class Connection(bool autoStreaming = true, Gate? startGate = null) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Disposed { get; } = Signal();
        public string Session { get; private set; } = "";
        public async Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (command == "hello")
            {
                Session = session;
                Emit(new { @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_ownership_guard", "application_capture", "member_volume" } });
            }
            else if (command == "start")
            {
                Session = session; Started.TrySetResult();
                if (startGate is not null) await startGate.WaitAsync(cancellation);
                if (autoStreaming) Emit(new { @event = "streaming" });
            }
            else if (command == "stop") _events.Writer.TryComplete();
        }
        public void Capture(object metrics, string? session = null, int version = 1)
            => Emit(new { @event = "capture_metrics", metrics }, session, version);
        public void Fault(bool retryable)
            => Emit(new { @event = "error", code = "synthetic_capture_fault", channel = "capture", message = "Synthetic capture fault", retryable });
        public void Emit(object payload, string? session = null, int version = 1)
        {
            var value = JsonSerializer.SerializeToNode(payload)!.AsObject();
            value["version"] = version; value["session_id"] = session ?? Session;
            if (!_events.Writer.TryWrite(JsonSerializer.SerializeToElement(value))) throw new InvalidOperationException("Synthetic engine emitted after disposal.");
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class Audio : IAudioService
    {
        public int MuteCalls;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId) { Interlocked.Increment(ref MuteCalls); return Task.CompletedTask; }
        public Task RestoreAsync() => Task.CompletedTask;
    }
}
