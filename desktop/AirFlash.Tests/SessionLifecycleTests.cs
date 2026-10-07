using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class SessionLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly SessionTiming Timing = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(1));
    private static Receiver Pod(string id = "a") => new(id, id, "192.0.2.10");
    private static AppSettings Settings() => new() { AutoConnectOnDiscover = false, ForceReconnect = false, MuteWhileStreaming = false, MasterVolume = 10 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRetargetCannotModifyDifferentOrSameIdReplacement(bool sameId)
    {
        var first = new Connection(); var replacement = new Connection();
        var factory = new Factory(first, replacement);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var stale = controller.Capture();
        var selected = Pod(sameId ? "a" : "b") with { Address = "192.0.2.20", Name = "Selected receiver" };
        await StartStreamingAsync(controller, replacement, selected);
        var current = controller.Capture();
        var changed = Settings(); changed.Options("stale").LatencyMode = "realtime";

        Assert.False(await controller.TryUpdateReceiverAsync(Pod("stale") with { Address = "192.0.2.30" }, changed, stale.Owner));

        Assert.NotEqual(stale.Owner, current.Owner);
        Assert.Same(current.Snapshot, controller.Capture().Snapshot);
        Assert.Equal(current.Owner, controller.Capture().Owner);
        Assert.Equal(selected, controller.Snapshot.Receiver);
        Assert.Equal(200, controller.Snapshot.TargetLatency);
        Assert.Equal(2, factory.OpenCount);
        Assert.False(replacement.Disposed);
        Assert.DoesNotContain(replacement.Commands, command => command.Name == "stop");
    }

    [Fact]
    public async Task StaleRetargetCannotCancelOrRetargetNewPairingWorker()
    {
        var first = new Connection(); var pairing = new Connection { RequestPin = true };
        var factory = new Factory(first, pairing);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var stale = controller.Capture();
        var pinEntered = NewSignal(); var pinReleased = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pinCancellation = default;
        try
        {
            await controller.PairAsync(Pod("b") with { Address = "192.0.2.20" }, Settings(), async (_, cancellation) =>
            {
                pinCancellation = cancellation; pinEntered.TrySetResult();
                return await pinReleased.Task.WaitAsync(cancellation);
            });
            await pinEntered.Task.WaitAsync(Deadline);
            var current = controller.Capture();

            Assert.False(await controller.TryUpdateReceiverAsync(Pod("stale"), Settings(), stale.Owner));

            Assert.Equal(PlaybackState.Pairing, current.Snapshot.State);
            Assert.NotEqual(stale.Owner, current.Owner);
            Assert.Same(current.Snapshot, controller.Snapshot);
            Assert.Equal("b", controller.Snapshot.Receiver!.Id);
            Assert.False(pinCancellation.IsCancellationRequested);
            Assert.False(pairing.Disposed);
            Assert.DoesNotContain(pairing.Commands, command => command.Name == "stop");
            Assert.Equal(2, factory.OpenCount);
        }
        finally { pinReleased.TrySetResult(null); }
    }

    [Fact]
    public async Task MetricsReplaceSnapshotButPreserveOwnerForIdentityPromotion()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var settings = Settings(); settings.Options("old").LatencyMode = "realtime";
        await StartStreamingAsync(controller, connection, Pod("old"), settings);
        var captured = controller.Capture();
        var metrics = SnapshotAsync(controller, snapshot => snapshot.Metrics?.Underruns == 7);
        connection.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = 7 } });
        await metrics;
        Assert.NotSame(captured.Snapshot, controller.Snapshot);
        Assert.Equal(captured.Owner, controller.Capture().Owner);
        ReceiverCatalog.ApplyMigrations(settings, new Dictionary<string, string> { ["old"] = "new" });

        Assert.True(await controller.TryUpdateReceiverAsync(Pod("new"), settings, captured.Owner));

        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal("new", controller.Snapshot.Receiver!.Id);
        Assert.Equal(120, controller.Snapshot.TargetLatency);
        Assert.Equal(7, controller.Snapshot.Metrics!.Underruns);
        Assert.Equal(1, factory.OpenCount);
        Assert.False(connection.Disposed);
        Assert.DoesNotContain(connection.Commands, command => command.Name == "stop");
    }

    [Fact]
    public async Task GuardedIdentityPromotionWithChangedLatencyRestartsExactlyOnce()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, first, Pod("old"));
        var captured = controller.Capture();
        var settings = Settings(); settings.Options("new").LatencyMode = "realtime";

        Assert.True(await controller.TryUpdateReceiverAsync(Pod("new"), settings, captured.Owner));
        await second.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == "new");

        Assert.NotEqual(captured.Owner, controller.Capture().Owner);
        Assert.True(first.Disposed);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(120, second.Commands.Single(command => command.Name == "start").Parameters.Number("latency_ms"));
        Assert.False(await controller.TryUpdateReceiverAsync(Pod("new"), settings, captured.Owner));
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task NativeRetryUsesFreshProcessButKeepsDesktopOwner()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var settings = Settings(); settings.ForceReconnect = true; settings.MaxReconnectAttempts = 1;
        await StartStreamingAsync(controller, first, Pod(), settings);
        var captured = controller.Capture();
        first.Emit(new { @event = "error", code = "peer_closed", channel = "events", message = "scripted lost connection", retryable = true });
        await second.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Diagnostics?.ReconnectCount == 1);

        Assert.True(first.Disposed);
        Assert.NotEqual(first.Session, second.Session);
        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal("peer_closed", controller.Snapshot.Diagnostics!.LastFault!.Code);
        Assert.True(await controller.TryUpdateReceiverAsync(Pod() with { Name = "Renamed after retry" }, settings, captured.Owner));
        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal("Renamed after retry", controller.Snapshot.Receiver!.Name);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task AutomaticStartAdmitsIdleAndDoesNotConsumeExplicitIntent()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();

        Assert.True(await controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, captured.AutomaticIntent));
        await connection.Started.Task.WaitAsync(Deadline);

        Assert.NotEqual(captured.Owner, controller.Capture().Owner);
        Assert.Equal(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
        Assert.Equal("a", controller.Snapshot.Receiver!.Id);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task AutomaticStartAdmitsErrorAndCleansPreviousAttempt()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var error = SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Error);
        first.Emit(new { @event = "error", code = "peer_closed", message = "scripted terminal fault", retryable = false });
        await error;
        var captured = controller.Capture();

        Assert.True(await controller.TryStartAutomaticAsync(Pod("b"), Settings(), captured.Owner, captured.AutomaticIntent));
        await second.Started.Task.WaitAsync(Deadline);

        Assert.True(first.Disposed);
        Assert.NotEqual(captured.Owner, controller.Capture().Owner);
        Assert.Equal("b", controller.Snapshot.Receiver!.Id);
        Assert.Equal(2, factory.OpenCount);
    }

    [Theory]
    [InlineData(PlaybackState.Connecting)]
    [InlineData(PlaybackState.Streaming)]
    [InlineData(PlaybackState.Standby)]
    [InlineData(PlaybackState.Pairing)]
    public async Task AutomaticStartRejectsEveryActiveState(PlaybackState state)
    {
        var connection = new Connection { AutoStreaming = state is PlaybackState.Streaming or PlaybackState.Standby };
        var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var settings = Settings(); settings.StandbyEnabled = state == PlaybackState.Standby;
        if (state == PlaybackState.Pairing)
        {
            await controller.PairAsync(Pod(), settings, (_, _) => Task.FromResult<string?>(null));
            await connection.PairSent.Task.WaitAsync(Deadline);
        }
        else
        {
            await controller.StartAsync(Pod(), settings);
            await connection.Started.Task.WaitAsync(Deadline);
            if (state == PlaybackState.Standby)
            {
                await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
                connection.Emit(new { @event = "capture_metrics", metrics = new { last_audio_qpc_ns = 1 } });
            }
        }
        await SnapshotAsync(controller, snapshot => snapshot.State == state);
        var captured = controller.Capture();

        Assert.False(await controller.TryStartAutomaticAsync(Pod("b"), settings, captured.Owner, captured.AutomaticIntent));

        Assert.Same(captured.Snapshot, controller.Snapshot);
        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal(1, factory.OpenCount);
        Assert.False(connection.Disposed);
        Assert.DoesNotContain(connection.Commands, command => command.Name == "stop");
    }

    [Fact]
    public async Task AutomaticStartRejectsOldOwnerEvenAfterReplacementReturnsToIdle()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        await StartStreamingAsync(controller, connection, Pod("b"));
        await controller.StopAsync();
        var current = controller.Capture();

        Assert.False(await controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, current.AutomaticIntent));

        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        Assert.Same(current.Snapshot, controller.Snapshot);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task AutomaticStartRejectsInvalidatedIntentWithoutAnOwnerChange()
    {
        var factory = new Factory();
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        controller.InvalidateAutomaticIntent();

        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
        Assert.False(await controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, captured.AutomaticIntent));
        Assert.Same(captured.Snapshot, controller.Snapshot);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticStartRejectsOfflineOrIncompleteReceiver(bool incomplete)
    {
        var factory = new Factory();
        await using var controller = new SessionController(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        var unavailable = incomplete
            ? Pod("stereo:incomplete") with { Members = [Pod()] }
            : Pod() with { Online = false };

        Assert.False(await controller.TryStartAutomaticAsync(unavailable, Settings(), captured.Owner, captured.AutomaticIntent));

        Assert.Equal(0, factory.OpenCount);
        Assert.Same(captured.Snapshot, controller.Snapshot);
        Assert.Equal(captured.Owner, controller.Capture().Owner);
    }

    [Fact]
    public async Task GuardedRetargetRechecksOwnerAfterWaitingForSerialization()
    {
        var first = new Connection(); var replacement = new Connection(); var factory = new Factory(first, replacement); var audio = new Audio();
        await using var controller = new SessionController(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        Task? change = null; Task<bool>? retarget = null;
        try
        {
            change = controller.StartAsync(Pod("b") with { Address = "192.0.2.20" }, Settings());
            await gate.Entered.Task.WaitAsync(Deadline);
            retarget = controller.TryUpdateReceiverAsync(Pod("stale"), Settings(), captured.Owner);
            Assert.False(change.IsCompleted);
            Assert.False(retarget.IsCompleted);
            Assert.NotEqual(captured.Owner, controller.Capture().Owner);
            gate.Release.TrySetResult();

            await change.WaitAsync(Deadline);
            Assert.False(await retarget.WaitAsync(Deadline));
            await replacement.Started.Task.WaitAsync(Deadline);
            Assert.Equal("b", controller.Snapshot.Receiver!.Id);
            Assert.False(replacement.Disposed);
            Assert.Equal(2, factory.OpenCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (change is not null) await change.WaitAsync(Deadline);
            if (retarget is not null) await retarget.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("pair")]
    [InlineData("stop")]
    public async Task ExplicitIntentInvalidatesBeforeWaitingForControllerSerialization(string action)
    {
        var connection = new Connection(); var audio = new Audio();
        await using var controller = new SessionController(new Factory(connection), audio, timing: Timing);
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        Task? operation = null; Task? holding = null;
        try
        {
            holding = controller.UpdateSettingsAsync(Settings());
            await gate.Entered.Task.WaitAsync(Deadline);
            operation = action switch
            {
                "start" => controller.StartAsync(Pod(), Settings()),
                "pair" => controller.PairAsync(Pod(), Settings(), (_, _) => Task.FromResult<string?>(null)),
                _ => controller.StopAsync()
            };

            Assert.False(operation.IsCompleted);
            Assert.Equal(captured.Owner, controller.Capture().Owner);
            Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (holding is not null) await holding.WaitAsync(Deadline);
            if (operation is not null) await operation.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task TwoQueuedAutomaticRequestsAdmitOnlyOne()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = new SessionController(factory, audio, timing: Timing);
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        Task<bool>? first = null; Task<bool>? second = null;
        try
        {
            first = controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, captured.AutomaticIntent);
            await gate.Entered.Task.WaitAsync(Deadline);
            second = controller.TryStartAutomaticAsync(Pod("b"), Settings(), captured.Owner, captured.AutomaticIntent);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            gate.Release.TrySetResult();

            Assert.True(await first.WaitAsync(Deadline));
            Assert.False(await second.WaitAsync(Deadline));
            await connection.Started.Task.WaitAsync(Deadline);
            Assert.Equal("a", controller.Snapshot.Receiver!.Id);
            Assert.Equal(1, factory.OpenCount);
            Assert.Single(connection.Commands, command => command.Name == "start");
            Assert.DoesNotContain(connection.Commands, command => command.Name == "stop");
        }
        finally
        {
            gate.Release.TrySetResult();
            if (first is not null) await first.WaitAsync(Deadline);
            if (second is not null) await second.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task StopWaitingForSerializationPreventsAutomaticCommit()
    {
        var factory = new Factory(); var audio = new Audio();
        await using var controller = new SessionController(factory, audio, timing: Timing);
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        Task<bool>? automatic = null; Task? stop = null;
        try
        {
            automatic = controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, captured.AutomaticIntent);
            await gate.Entered.Task.WaitAsync(Deadline);
            stop = controller.StopAsync();

            Assert.False(stop.IsCompleted);
            Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            Assert.Equal(0, factory.OpenCount);
            gate.Release.TrySetResult();

            Assert.False(await automatic.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
            Assert.Null(controller.Snapshot.Receiver);
            Assert.Equal(0, factory.OpenCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (automatic is not null) await automatic.WaitAsync(Deadline);
            if (stop is not null) await stop.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task IntentInvalidatedDuringOldErrorCleanupRejectsAutomaticStart()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = new SessionController(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var errorPublished = NewSignal(); Gate? gate = null; Task<bool>? automatic = null;
        void HoldErrorCleanup(SessionSnapshot snapshot)
        {
            if (snapshot.State != PlaybackState.Error) return;
            gate ??= audio.PauseNextRestore(); errorPublished.TrySetResult();
        }
        controller.Changed += HoldErrorCleanup;
        try
        {
            connection.Emit(new { @event = "error", code = "peer_closed", message = "scripted terminal fault", retryable = false });
            await errorPublished.Task.WaitAsync(Deadline);
            Assert.NotNull(gate);
            await gate.Entered.Task.WaitAsync(Deadline);
            var captured = controller.Capture();
            Assert.Equal(PlaybackState.Error, captured.Snapshot.State);
            automatic = controller.TryStartAutomaticAsync(Pod("b"), Settings(), captured.Owner, captured.AutomaticIntent);
            Assert.False(automatic.IsCompleted);
            Assert.Equal(1, factory.OpenCount);

            controller.InvalidateAutomaticIntent();
            gate.Release.TrySetResult();

            Assert.False(await automatic.WaitAsync(Deadline));
            Assert.True(connection.Disposed);
            Assert.Equal(1, factory.OpenCount);
            Assert.NotEqual("b", controller.Snapshot.Receiver?.Id);
            Assert.Equal("peer_closed", controller.Snapshot.Diagnostics!.LastFault!.Code);
        }
        finally
        {
            controller.Changed -= HoldErrorCleanup;
            gate?.Release.TrySetResult();
            if (automatic is not null) await automatic.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRetargetDoesNotRestoreNewOwnersSavedMuteBit(bool initiallyMuted)
    {
        var first = new Connection(); var second = new Connection(); var audio = new Audio(initiallyMuted);
        await using var controller = new SessionController(new Factory(first, second), audio, timing: Timing);
        var settings = Settings(); settings.MuteWhileStreaming = true;
        await StartStreamingAsync(controller, first, Pod(), settings);
        var stale = controller.Capture();
        await StartStreamingAsync(controller, second, Pod("b"), settings);
        var restores = audio.RestoreCount;

        Assert.False(await controller.TryUpdateReceiverAsync(Pod("stale"), settings, stale.Owner));

        Assert.True(audio.Muted);
        Assert.Equal(restores, audio.RestoreCount);
        Assert.False(second.Disposed);
        await controller.StopAsync();
        Assert.Equal(initiallyMuted, audio.Muted);
    }

    private static async Task StartStreamingAsync(SessionController controller, Connection connection, Receiver receiver, AppSettings? settings = null)
    {
        await controller.StartAsync(receiver, settings ?? Settings());
        await connection.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == receiver.Id);
    }

    private static async Task<SessionSnapshot> SnapshotAsync(SessionController controller, Func<SessionSnapshot, bool> predicate)
    {
        var observed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionSnapshot snapshot) { if (predicate(snapshot)) observed.TrySetResult(snapshot); }
        controller.Changed += OnChanged;
        try
        {
            var current = controller.Snapshot;
            if (predicate(current)) observed.TrySetResult(current);
            return await observed.Task.WaitAsync(Deadline);
        }
        finally { controller.Changed -= OnChanged; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
    }

    private sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _connections = new(connections);
        private int _openCount;
        public int OpenCount => Volatile.Read(ref _openCount);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref _openCount);
            return _connections.TryDequeue(out var connection) ? connection : throw new IOException("Unexpected fake engine admission.");
        }
    }

    private sealed record Command(string Name, JsonElement Parameters);
    private sealed class Connection : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private volatile string _session = "";
        private volatile bool _disposed;
        public bool AutoStreaming { get; init; } = true;
        public bool RequestPin { get; init; }
        public string Session => _session;
        public bool Disposed => _disposed;
        public ConcurrentQueue<Command> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource PairSent { get; } = NewSignal();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue(new(command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            switch (command)
            {
                case "start":
                    _session = session; Started.TrySetResult();
                    if (AutoStreaming) Emit(new { @event = "streaming" });
                    break;
                case "pair":
                    _session = session; PairSent.TrySetResult();
                    if (RequestPin) Emit(new { @event = "pin_required" });
                    break;
                case "stop": _events.Writer.TryComplete(); break;
            }
            return Task.CompletedTask;
        }
        public void Emit(object payload)
        {
            var envelope = JsonSerializer.SerializeToNode(payload)!.AsObject();
            envelope["version"] = 1; envelope["session_id"] = _session;
            Assert.True(_events.Writer.TryWrite(JsonSerializer.SerializeToElement(envelope)));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync()
        {
            _disposed = true; _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Audio(bool initiallyMuted = false) : IAudioService
    {
        private readonly object _sync = new();
        private readonly ConcurrentQueue<Gate> _restoreGates = new();
        private bool _muted = initiallyMuted;
        private bool? _savedMute;
        private int _restoreCount;
        public bool Muted { get { lock (_sync) return _muted; } }
        public int RestoreCount => Volatile.Read(ref _restoreCount);
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("fake-endpoint");
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId)
        {
            lock (_sync) { _savedMute ??= _muted; _muted = true; }
            return Task.CompletedTask;
        }
        public Gate PauseNextRestore()
        {
            var gate = new Gate(); _restoreGates.Enqueue(gate); return gate;
        }
        public async Task RestoreAsync()
        {
            Interlocked.Increment(ref _restoreCount);
            if (_restoreGates.TryDequeue(out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            }
            lock (_sync)
            {
                if (_savedMute is not { } original) return;
                _muted = original; _savedMute = null;
            }
        }
    }
}
