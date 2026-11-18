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





    [Fact]
    public async Task NativeRetryUsesFreshProcessButKeepsDesktopOwner()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
        Assert.True(await controller.TryUpdateReceiverAsync(Pod() with { Name = "Renamed after retry" }, settings, captured));
        Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal("Renamed after retry", controller.Snapshot.Receiver!.Name);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task AutomaticStartAdmitsIdleAndDoesNotConsumeExplicitIntent()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
                connection.Emit(new { @event = "standby", reason = "silence" });
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
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        var unavailable = incomplete
            ? Pod("stereo:incomplete") with { Members = [Pod()] }
            : Pod() with { Online = false };

        Assert.False(await controller.TryStartAutomaticAsync(unavailable, Settings(), captured.Owner, captured.AutomaticIntent));

        Assert.Equal(0, factory.OpenCount);
        Assert.Same(captured.Snapshot, controller.Snapshot);
        Assert.Equal(captured.Owner, controller.Capture().Owner);
    }


    [Theory]
    [InlineData("start")]
    [InlineData("pair")]
    [InlineData("stop")]
    public async Task ExplicitIntentInvalidatesBeforeWaitingForControllerSerialization(string action)
    {
        var connection = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
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

    [Theory]
    [InlineData("start", "pair", false)]
    [InlineData("start", "pair", true)]
    [InlineData("pair", "pair", false)]
    [InlineData("pair", "pair", true)]
    [InlineData("start", "stop", false)]
    [InlineData("pair", "stop", false)]
    [InlineData("start", "rejected pair", false)]
    [InlineData("pair", "rejected pair", false)]
    public async Task SupersededExplicitRequestCannotCommitAfterSerialization(string pendingAction, string newerAction, bool sameId)
    {
        var selected = new Connection(); var factory = new Factory(selected); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var obsolete = Pod("obsolete");
        var replacement = Pod(sameId ? obsolete.Id : "selected") with { Address = "192.0.2.20", Port = 7001 };
        var publications = new ConcurrentQueue<SessionSnapshot>(); controller.Changed += publications.Enqueue;
        var gate = audio.PauseNextRestore();
        Task? holding = null; Task? pending = null; Task? newer = null;
        try
        {
            holding = controller.UpdateSettingsAsync(Settings());
            await gate.Entered.Task.WaitAsync(Deadline);
            pending = pendingAction == "start" ? controller.StartAsync(obsolete, Settings())
                : controller.PairAsync(obsolete, Settings(), (_, _) => Task.FromResult<string?>(null));
            Assert.False(pending.IsCompleted);
            if (newerAction == "rejected pair")
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PairAsync(replacement with { Online = false }, Settings(), (_, _) => Task.FromResult<string?>(null)));
            else newer = newerAction == "pair" ? controller.PairAsync(replacement, Settings(), (_, _) => Task.FromResult<string?>(null))
                : controller.StopAsync();
            gate.Release.TrySetResult();
            await holding.WaitAsync(Deadline); await pending.WaitAsync(Deadline);
            if (newer is not null) await newer.WaitAsync(Deadline);
            Assert.DoesNotContain(publications, snapshot => snapshot.Receiver == obsolete);
            if (newerAction == "pair") await selected.PairSent.Task.WaitAsync(Deadline);
            Assert.Equal(newerAction == "pair" ? 1 : 0, factory.OpenCount);
            Assert.Equal(newerAction == "pair" ? PlaybackState.Pairing : PlaybackState.Idle, controller.Snapshot.State);
            Assert.Equal(newerAction == "pair" ? replacement : null, controller.Snapshot.Receiver);
            Assert.DoesNotContain(selected.Commands, command => command.Name == "start");
        }
        finally
        {
            gate.Release.TrySetResult();
            if (holding is not null) await holding.WaitAsync(Deadline);
            if (pending is not null) await pending.WaitAsync(Deadline);
            if (newer is not null) await newer.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("start", "pair")]
    [InlineData("pair", "pair")]
    [InlineData("start", "stop")]
    [InlineData("pair", "stop")]
    public async Task NewerExplicitIntentDuringCleanupPreventsSupersededCommit(string pendingAction, string newerAction)
    {
        var selected = new Connection(); var factory = new Factory(selected); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var obsolete = Pod("obsolete"); var replacement = Pod("selected") with { Address = "192.0.2.20" };
        var publications = new ConcurrentQueue<SessionSnapshot>(); controller.Changed += publications.Enqueue;
        var gate = audio.PauseNextRestore(); Task? pending = null; Task? newer = null;
        try
        {
            pending = pendingAction == "start" ? controller.StartAsync(obsolete, Settings())
                : controller.PairAsync(obsolete, Settings(), (_, _) => Task.FromResult<string?>(null));
            await gate.Entered.Task.WaitAsync(Deadline);
            newer = newerAction == "pair" ? controller.PairAsync(replacement, Settings(), (_, _) => Task.FromResult<string?>(null))
                : controller.StopAsync();
            gate.Release.TrySetResult(); await pending.WaitAsync(Deadline); await newer.WaitAsync(Deadline);
            Assert.DoesNotContain(publications, snapshot => snapshot.Receiver == obsolete);
            if (newerAction == "pair") await selected.PairSent.Task.WaitAsync(Deadline);
            Assert.Equal(newerAction == "pair" ? 1 : 0, factory.OpenCount);
            Assert.Equal(newerAction == "pair" ? replacement : null, controller.Snapshot.Receiver);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (pending is not null) await pending.WaitAsync(Deadline);
            if (newer is not null) await newer.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task StopWaitingForSerializationPreventsAutomaticCommit()
    {
        var factory = new Factory(); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
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
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
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
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
            Assert.Null(controller.Snapshot.Receiver);
            Assert.Equal(0, controller.Snapshot.StreamRate);
            Assert.Same(captured.Snapshot.Diagnostics!.LastFault, controller.Snapshot.Diagnostics!.LastFault);
        }
        finally
        {
            controller.Changed -= HoldErrorCleanup;
            gate?.Release.TrySetResult();
            if (automatic is not null) await automatic.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task EligibilityArrivalWhileWaitingForSerializationRejectsAutomaticAdmissionWithoutNewIntent()
    {
        var factory = new Factory(); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var gate = audio.PauseNextRestore(); Task? holding = null; Task<bool>? automatic = null;
        var revision = 0; var admissionChecks = 0;
        try
        {
            holding = controller.UpdateSettingsAsync(Settings());
            await gate.Entered.Task.WaitAsync(Deadline);
            var captured = controller.Capture(); var eligibleRevision = Volatile.Read(ref revision);
            automatic = controller.TryStartAutomaticAsync(Pod(), Settings(), captured.Owner, captured.AutomaticIntent,
                () => { Interlocked.Increment(ref admissionChecks); return Volatile.Read(ref revision) == eligibleRevision; });
            Assert.False(automatic.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref admissionChecks));

            Interlocked.Increment(ref revision);
            Assert.Equal(captured.Owner, controller.Capture().Owner);
            Assert.Equal(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            gate.Release.TrySetResult();

            await holding.WaitAsync(Deadline);
            Assert.False(await automatic.WaitAsync(Deadline));
            Assert.Equal(1, Volatile.Read(ref admissionChecks));
            Assert.Same(captured.Snapshot, controller.Snapshot);
            Assert.Equal(0, factory.OpenCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (holding is not null) await holding.WaitAsync(Deadline);
            if (automatic is not null) await automatic.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task EligibilityArrivalDuringOldErrorCleanupRejectsAutomaticCommitWithoutNewIntent()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var errorPublished = NewSignal(); Gate? gate = null; Task<bool>? automatic = null;
        var revision = 0; var admissionChecks = 0;
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
            var captured = controller.Capture(); var eligibleRevision = Volatile.Read(ref revision);
            automatic = controller.TryStartAutomaticAsync(Pod("b"), Settings(), captured.Owner, captured.AutomaticIntent,
                () => { Interlocked.Increment(ref admissionChecks); return Volatile.Read(ref revision) == eligibleRevision; });
            Assert.False(automatic.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref admissionChecks));

            Interlocked.Increment(ref revision);
            Assert.Equal(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            gate.Release.TrySetResult();

            Assert.False(await automatic.WaitAsync(Deadline));
            Assert.Equal(2, Volatile.Read(ref admissionChecks));
            Assert.True(connection.Disposed); Assert.Equal(1, factory.OpenCount);
            Assert.Single(connection.Commands, command => command.Name == "start");
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
            Assert.Null(controller.Snapshot.Receiver);
            Assert.Equal(0, controller.Snapshot.StreamRate);
            Assert.Same(captured.Snapshot.Diagnostics!.LastFault, controller.Snapshot.Diagnostics!.LastFault);
            Assert.Equal(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
        }
        finally
        {
            controller.Changed -= HoldErrorCleanup;
            gate?.Release.TrySetResult();
            if (automatic is not null) await automatic.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("stop")]
    public async Task NewExplicitIntentDuringGuardedReceiverEditCleanupPreventsSupersededRestart(string action)
    {
        var first = new Connection(); var replacement = new Connection(); var factory = new Factory(first, replacement); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        var obsolete = Pod() with { Address = "192.0.2.30" };
        var obsoletePublished = false;
        void Observe(SessionSnapshot snapshot)
        {
            if (snapshot.IsActive && snapshot.Receiver?.Address == obsolete.Address) obsoletePublished = true;
        }
        controller.Changed += Observe;
        Task<bool>? retarget = null; Task? explicitAction = null;
        try
        {
            retarget = controller.TryUpdateReceiverAsync(obsolete, Settings(), captured);
            await gate.Entered.Task.WaitAsync(Deadline);
            explicitAction = action == "pair" ? controller.PairAsync(Pod("b"), Settings(), (_, _) => Task.FromResult<string?>(null)) : controller.StopAsync();
            Assert.False(explicitAction.IsCompleted);
            Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            gate.Release.TrySetResult();
            var admitted = await retarget.WaitAsync(Deadline); await explicitAction.WaitAsync(Deadline);
            Assert.False(admitted);
            Assert.False(obsoletePublished);
            Assert.Equal(action == "pair" ? PlaybackState.Pairing : PlaybackState.Idle, controller.Snapshot.State);
            Assert.Equal(action == "pair" ? "b" : null, controller.Snapshot.Receiver?.Id);
        }
        finally
        {
            controller.Changed -= Observe; gate.Release.TrySetResult();
            if (retarget is not null) await retarget.WaitAsync(Deadline);
            if (explicitAction is not null) await explicitAction.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("stop")]
    public async Task NewExplicitIntentDuringSettingsCleanupPreventsSupersededRestart(string action)
    {
        var first = new Connection(); var replacement = new Connection(); var factory = new Factory(first, replacement); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        var settings = Settings(); settings.LatencyMode = "buffered"; settings.Options("a").SampleRate = "48000";
        var obsoletePublished = false;
        void Observe(SessionSnapshot snapshot)
        {
            if (snapshot.IsActive && snapshot.Receiver?.Id == "a" && snapshot.TargetLatency == 500) obsoletePublished = true;
        }
        controller.Changed += Observe;
        Task? update = null; Task? explicitAction = null;
        try
        {
            update = controller.UpdateSettingsAsync(settings);
            await gate.Entered.Task.WaitAsync(Deadline);
            explicitAction = action == "pair" ? controller.PairAsync(Pod("b"), Settings(), (_, _) => Task.FromResult<string?>(null)) : controller.StopAsync();
            Assert.False(explicitAction.IsCompleted);
            Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            gate.Release.TrySetResult();
            await update.WaitAsync(Deadline); await explicitAction.WaitAsync(Deadline);
            Assert.False(obsoletePublished);
            Assert.Equal(action == "pair" ? PlaybackState.Pairing : PlaybackState.Idle, controller.Snapshot.State);
            Assert.Equal(action == "pair" ? "b" : null, controller.Snapshot.Receiver?.Id);
            if (action == "stop") Assert.Equal(0, controller.Snapshot.StreamRate);
        }
        finally
        {
            controller.Changed -= Observe; gate.Release.TrySetResult();
            if (update is not null) await update.WaitAsync(Deadline);
            if (explicitAction is not null) await explicitAction.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(PlaybackState.Connecting, true)]
    [InlineData(PlaybackState.Streaming, true)]
    [InlineData(PlaybackState.Standby, true)]
    [InlineData(PlaybackState.Pairing, false)]
    [InlineData(PlaybackState.Error, false)]
    [InlineData(PlaybackState.Idle, false)]
    public async Task EndpointRestartAdmitsOnlyOwnedActiveNonPairingState(PlaybackState state, bool admitted)
    {
        var first = new Connection { AutoStreaming = state is not PlaybackState.Connecting };
        var second = new Connection(); var factory = new Factory(first, second); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var settings = Settings(); settings.StandbyEnabled = state == PlaybackState.Standby;
        if (state == PlaybackState.Pairing)
        {
            await controller.PairAsync(Pod(), settings, (_, _) => Task.FromResult<string?>(null));
            await first.PairSent.Task.WaitAsync(Deadline);
        }
        else if (state != PlaybackState.Idle)
        {
            await controller.StartAsync(Pod(), settings); await first.Started.Task.WaitAsync(Deadline);
            if (state == PlaybackState.Standby)
            {
                await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
                first.Emit(new { @event = "standby", reason = "silence" });
            }
            else if (state == PlaybackState.Error)
            {
                await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
                first.Emit(new { @event = "error", message = "scripted endpoint precondition", retryable = false });
            }
        }
        await SnapshotAsync(controller, snapshot => snapshot.State == state);
        var captured = controller.Capture(); var restores = audio.RestoreCount;

        Assert.Equal(admitted, await controller.TryRestartForEndpointAsync(settings, captured.Owner, captured.AutomaticIntent));

        if (admitted)
        {
            await second.Started.Task.WaitAsync(Deadline);
            Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed);
            Assert.NotEqual(captured.Owner, controller.Capture().Owner);
            Assert.Equal(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            Assert.Equal("a", controller.Snapshot.Receiver!.Id);
        }
        else
        {
            Assert.Same(captured.Snapshot, controller.Snapshot);
            Assert.Equal(state == PlaybackState.Idle ? 0 : 1, factory.OpenCount);
            if (state != PlaybackState.Error) Assert.Equal(restores, audio.RestoreCount);
        }
    }

    [Fact]
    public async Task EndpointRestartUsesCurrentOwnedIdentityAfterSameOwnerPromotion()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, first, Pod("old"));
        var captured = controller.Capture();
        var promoted = Pod("new") with { Name = "Promoted owner" };
        Assert.True(await controller.TryUpdateReceiverAsync(promoted, Settings(), captured));
        Assert.Equal(captured.Owner, controller.Capture().Owner);

        Assert.True(await controller.TryRestartForEndpointAsync(Settings(), captured.Owner, captured.AutomaticIntent));
        await second.Started.Task.WaitAsync(Deadline);

        Assert.Equal(promoted, controller.Snapshot.Receiver);
        Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed);
        Assert.Equal("192.0.2.10", second.Commands.Single(command => command.Name == "start").Parameters.GetProperty("peers")[0].Text("host"));
    }

    [Fact]
    public async Task PassiveEndpointAndRetargetCallsRejectOldIntentWithoutOwnerChange()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var captured = controller.Capture(); var restores = audio.RestoreCount;
        controller.InvalidateAutomaticIntent();

        Assert.False(await controller.TryRestartForEndpointAsync(Settings(), captured.Owner, captured.AutomaticIntent));
        Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
        Assert.False(await controller.TryUpdateReceiverAsync(Pod() with { Name = "Obsolete metadata" }, Settings(), captured));

        Assert.Same(captured.Snapshot, controller.Snapshot); Assert.Equal(captured.Owner, controller.Capture().Owner);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(restores, audio.RestoreCount); Assert.False(connection.Disposed);
    }

    [Fact]
    public async Task PassiveEndpointCallsCannotTouchSameIdReplacementOwner()
    {
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod()); var captured = controller.Capture();
        await StartStreamingAsync(controller, second, Pod() with { Address = "192.0.2.20" });
        var current = controller.Capture(); var restores = audio.RestoreCount;

        Assert.False(await controller.TryRestartForEndpointAsync(Settings(), captured.Owner, current.AutomaticIntent));
        Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, current.AutomaticIntent));

        Assert.Same(current.Snapshot, controller.Snapshot); Assert.Equal(2, factory.OpenCount);
        Assert.Equal(restores, audio.RestoreCount); Assert.False(second.Disposed);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("stop")]
    public async Task NewExplicitIntentDuringEndpointCleanupPreventsSupersededRestart(string action)
    {
        var first = new Connection(); var replacement = new Connection(); var factory = new Factory(first, replacement); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var captured = controller.Capture(); var gate = audio.PauseNextRestore(); var obsoletePublished = false;
        void Observe(SessionSnapshot snapshot)
        {
            if (snapshot.IsActive && snapshot.Receiver?.Id == "a" && controller.Capture().Owner != captured.Owner) obsoletePublished = true;
        }
        controller.Changed += Observe; Task<bool>? endpoint = null; Task? explicitAction = null;
        try
        {
            endpoint = controller.TryRestartForEndpointAsync(Settings(), captured.Owner, captured.AutomaticIntent);
            await gate.Entered.Task.WaitAsync(Deadline);
            explicitAction = action == "pair" ? controller.PairAsync(Pod("b"), Settings(), (_, _) => Task.FromResult<string?>(null)) : controller.StopAsync();
            Assert.False(explicitAction.IsCompleted); gate.Release.TrySetResult();

            Assert.False(await endpoint.WaitAsync(Deadline)); await explicitAction.WaitAsync(Deadline);

            Assert.False(obsoletePublished);
            Assert.Equal(action == "pair" ? PlaybackState.Pairing : PlaybackState.Idle, controller.Snapshot.State);
            Assert.Equal(action == "pair" ? "b" : null, controller.Snapshot.Receiver?.Id);
        }
        finally
        {
            controller.Changed -= Observe; gate.Release.TrySetResult();
            if (endpoint is not null) await endpoint.WaitAsync(Deadline);
            if (explicitAction is not null) await explicitAction.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerCleanupFailureIsVisibleAndStopJoinsMuteRestorationBeforeRetry(bool originallyMuted)
    {
        var connection = new Connection { DisposeFailures = 2 };
        var factory = new Factory(connection);
        var audio = new Audio(originallyMuted);
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var (receiver, settings) = originallyMuted ? SoftwareGroup() : (Pod(), Settings());
        settings.MuteWhileStreaming = true;
        await StartStreamingAsync(controller, connection, receiver, settings);
        Assert.True(audio.Muted);
        var restore = audio.PauseNextRestore();
        var restorationObserved = audio.ObserveNextRestore();
        var restoredBeforeRetry = false;
        connection.BeforeDispose = attempt =>
        {
            if (attempt == 2) restoredBeforeRetry = restorationObserved.Task.IsCompletedSuccessfully && audio.Muted == originallyMuted;
        };
        Task? stop = null;
        try
        {
            connection.Emit(new { @event = "error", code = "peer_closed", message = "synthetic terminal fault", retryable = false });
            await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Error);
            await restore.Entered.Task.WaitAsync(Deadline);
            Assert.True(controller.NeedsCleanup);
            Assert.Equal(receiver.Id, controller.Snapshot.Receiver?.Id);
            Assert.Equal("engine_cleanup_failed", controller.Snapshot.Diagnostics?.LastFault?.Code);
            var failed = controller.Capture();
            if (receiver.IsSoftwareGroup)
                Assert.False(await controller.StopForDiscoveryConflictAsync(failed.Snapshot).WaitAsync(Deadline));
            Assert.False(await controller.TryStartAutomaticAsync(Pod("b"), settings, failed.Owner, failed.AutomaticIntent).WaitAsync(Deadline));
            Assert.False(await controller.TryStartExplicitAsync(Pod("b"), settings, controller.BeginExplicitIntent()).WaitAsync(Deadline));
            Assert.Equal(failed.Owner, controller.Capture().Owner);
            stop = controller.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(1, connection.DisposeCalls);

            restore.Release.TrySetResult();
            await Assert.ThrowsAsync<EngineShutdownException>(() => stop.WaitAsync(Deadline));
            stop = null;
            Assert.Equal(2, connection.DisposeCalls);
            Assert.True(restoredBeforeRetry);
            Assert.Equal(originallyMuted, audio.Muted);
            Assert.True(controller.NeedsCleanup);

            await controller.StopAsync().WaitAsync(Deadline);
            Assert.Equal(3, connection.DisposeCalls);
            Assert.True(connection.Disposed);
            Assert.Equal(1, factory.OpenCount);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
            Assert.False(controller.NeedsCleanup);
            Assert.Equal(originallyMuted, audio.Muted);
        }
        finally
        {
            restore.Release.TrySetResult();
            connection.DisposeFailures = 0;
            try { await (stop ?? controller.StopAsync()).WaitAsync(Deadline); }
            catch (EngineShutdownException) { await controller.StopAsync().WaitAsync(Deadline); }
        }
    }

    [Fact]
    public async Task InactiveEndpointRestoreSerializesBeforeNewOwnerCanMute()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await audio.MuteAsync("fake-endpoint");
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        Task<bool>? restore = null; Task? start = null;
        try
        {
            restore = controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent);
            await gate.Entered.Task.WaitAsync(Deadline);
            var settings = Settings(); settings.MuteWhileStreaming = true;
            start = controller.StartAsync(Pod("b"), settings);
            Assert.False(start.IsCompleted); Assert.Equal(0, factory.OpenCount); Assert.Equal(captured.Owner, controller.Capture().Owner);
            gate.Release.TrySetResult(); Assert.True(await restore.WaitAsync(Deadline)); await start.WaitAsync(Deadline);
            await connection.Started.Task.WaitAsync(Deadline); await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
            Assert.True(audio.Muted); Assert.Equal("b", controller.Snapshot.Receiver!.Id);
            await controller.StopAsync(); Assert.False(audio.Muted);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (restore is not null) await restore.WaitAsync(Deadline);
            if (start is not null) await start.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task InactiveEndpointRestorePropagatesFailureAndAllowsRecoveryRetry()
    {
        var audio = new Audio(); await using var controller = TestSessionController.Create(new Factory(), audio, timing: Timing);
        await audio.MuteAsync("fake-endpoint"); var captured = controller.Capture(); audio.FailRestore = true;

        await Assert.ThrowsAsync<IOException>(() => controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));

        Assert.True(audio.Muted); Assert.Equal(captured.Owner, controller.Capture().Owner);
        audio.FailRestore = false;
        Assert.True(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
        Assert.False(audio.Muted); Assert.Equal(2, audio.RestoreCount);
    }

    [Fact]
    public async Task EndpointRestoreRejectsInactiveSnapshotWhileWorkerCleanupIsUnsettled()
    {
        var first = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(first), audio, timing: Timing);
        await StartStreamingAsync(controller, first, Pod());
        var gate = audio.PauseNextRestore();
        try
        {
            var error = SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Error);
            first.Emit(new { @event = "error", message = "scripted terminal failure", retryable = false });
            await gate.Entered.Task.WaitAsync(Deadline);
            // RunStream restores before publishing Error; release that first restore and hold the worker's final restore.
            var final = audio.PauseNextRestore();
            try
            {
                gate.Release.TrySetResult();
                await error;
                await final.Entered.Task.WaitAsync(Deadline); var captured = controller.Capture(); var restores = audio.RestoreCount;
                Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
                Assert.Equal(restores, audio.RestoreCount); Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
            }
            finally { final.Release.TrySetResult(); }
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("endpoint", "invalidate")]
    [InlineData("retarget", "invalidate")]
    [InlineData("settings", "invalidate")]
    [InlineData("endpoint", "offline start")]
    [InlineData("retarget", "offline start")]
    [InlineData("settings", "offline start")]
    [InlineData("endpoint", "incomplete pair")]
    [InlineData("retarget", "incomplete pair")]
    [InlineData("settings", "incomplete pair")]
    public async Task InvalidatedCleanupWithoutReplacementPublishesTruthfulIdle(string operation, string rejection)
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var captured = controller.Capture(); var gate = audio.PauseNextRestore();
        var settings = Settings(); settings.Options("a").SampleRate = "48000";
        Task? cleanup = null;
        try
        {
            cleanup = operation switch
            {
                "endpoint" => controller.TryRestartForEndpointAsync(settings, captured.Owner, captured.AutomaticIntent),
                "retarget" => controller.TryUpdateReceiverAsync(Pod() with { Address = "192.0.2.30" }, settings, captured),
                _ => controller.UpdateSettingsAsync(settings)
            };
            await gate.Entered.Task.WaitAsync(Deadline);
            if (rejection == "offline start")
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Pod("rejected") with { Online = false }, Settings()));
            else if (rejection == "incomplete pair")
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PairAsync(Pod("stereo:rejected") with { Members = [Pod()] }, Settings(), (_, _) => Task.FromResult<string?>(null)));
            else controller.InvalidateAutomaticIntent();
            gate.Release.TrySetResult(); await cleanup.WaitAsync(Deadline);

            Assert.False(controller.Snapshot.IsActive);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Receiver);
            Assert.True(connection.Disposed); Assert.Equal(1, factory.OpenCount);
            Assert.Equal(0, controller.Snapshot.StreamRate);
        }
        finally { gate.Release.TrySetResult(); if (cleanup is not null) await cleanup.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task EndpointReturnDuringLastWorkerRestoreSchedulesOneRecoveryAfterCompletion()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        SessionController? controller = null;
        var notification = NewSignal(); TaskCompletionSource? recovered = null;
        var failureRestoreCount = 0; var deferredCalls = new List<Task<bool>>();
        void Log(string message)
        {
            if (!message.StartsWith("Failed to restore local mute state:", StringComparison.Ordinal) || controller!.Snapshot.State != PlaybackState.Error) return;
            audio.FailRestore = false; recovered = audio.ObserveNextRestore(); failureRestoreCount = audio.RestoreCount;
            var captured = controller.Capture();
            for (var i = 0; i < 10; i++) deferredCalls.Add(controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            notification.TrySetResult();
        }
        controller = TestSessionController.Create(factory, audio, Log, Timing);
        await using (controller)
        {
            var settings = Settings(); settings.MuteWhileStreaming = true;
            await StartStreamingAsync(controller, connection, Pod(), settings); audio.FailRestore = true;
            connection.Emit(new { @event = "error", message = "scripted endpoint unavailable", retryable = false });
            await notification.Task.WaitAsync(Deadline);
            Assert.All(deferredCalls, task => Assert.True(task.IsCompletedSuccessfully && !task.Result));
            Assert.NotNull(recovered); await recovered.Task.WaitAsync(Deadline);
            Assert.Equal(failureRestoreCount + 1, audio.RestoreCount); Assert.False(audio.Muted);
            Assert.Equal(1, factory.OpenCount); Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredEndpointRestoreRejectsSupersededIntentOrOwner(bool replaceOwner)
    {
        var first = new Connection(); var second = new Connection { AutoStreaming = false }; var factory = new Factory(first, second); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var final = await HoldFinalRestoreAsync(controller, first, audio);
        Task? replacement = null;
        try
        {
            var captured = controller.Capture();
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            var consumer = DeferredRecovery(controller); var restores = audio.RestoreCount;
            if (replaceOwner) replacement = controller.StartAsync(Pod("b"), Settings());
            else controller.InvalidateAutomaticIntent();
            final.Release.TrySetResult(); await consumer.WaitAsync(Deadline);
            if (replacement is not null)
            {
                await replacement.WaitAsync(Deadline); await second.Started.Task.WaitAsync(Deadline);
                // Join stale recovery before allowing the new worker's legitimate
                // Streaming restore (MuteWhileStreaming=false). Started alone can
                // race that restore and is not a stable operation-count boundary.
                Assert.Equal(restores + 1, audio.RestoreCount); Assert.Equal(2, factory.OpenCount);
                Assert.Equal(PlaybackState.Connecting, controller.Snapshot.State);
                Assert.Equal("b", controller.Snapshot.Receiver?.Id);
                Assert.NotEqual(captured.Owner, controller.Capture().Owner);
                second.Emit(new { @event = "streaming" });
                await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == "b");
                Assert.Equal(restores + 2, audio.RestoreCount);
            }
            else
            {
                Assert.Equal(restores, audio.RestoreCount); Assert.Equal(1, factory.OpenCount);
                Assert.Equal(captured.Owner, controller.Capture().Owner);
                Assert.NotEqual(captured.AutomaticIntent, controller.Capture().AutomaticIntent);
            }
        }
        finally { final.Release.TrySetResult(); if (replacement is not null) await replacement.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task FreshEndpointIntentReplacesStaleRecoveryForTheSameWorker()
    {
        var connection = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
        var final = await HoldFinalRestoreAsync(controller, connection, audio);
        try
        {
            var old = controller.Capture();
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(old.Owner, old.AutomaticIntent));
            var consumer = DeferredRecovery(controller);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Pod("rejected") with { Online = false }, Settings()));
            var current = controller.Capture(); var restores = audio.RestoreCount;
            Assert.Equal(old.Owner, current.Owner); Assert.NotEqual(old.AutomaticIntent, current.AutomaticIntent);
            for (var i = 0; i < 10; ++i)
                Assert.False(await controller.TryRestoreAudioIfInactiveAsync(current.Owner, current.AutomaticIntent));
            final.Release.TrySetResult(); await consumer.WaitAsync(Deadline);
            Assert.Equal(restores + 1, audio.RestoreCount); Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
        }
        finally { final.Release.TrySetResult(); }
    }

    [Fact]
    public async Task DeferredEndpointFailureIsObservableAndDoesNotFaultItsConsumer()
    {
        var connection = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
        var final = await HoldFinalRestoreAsync(controller, connection, audio);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.EndpointRecoveryFailed += error => failure.TrySetResult(error);
        try
        {
            var captured = controller.Capture(); audio.FailRestore = true; var restores = audio.RestoreCount;
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            var consumer = DeferredRecovery(controller); final.Release.TrySetResult();
            Assert.IsType<IOException>(await failure.Task.WaitAsync(Deadline)); await consumer.WaitAsync(Deadline);
            Assert.Equal(restores + 1, audio.RestoreCount); Assert.Equal(captured.Owner, controller.Capture().Owner);
            audio.FailRestore = false;
            Assert.True(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
        }
        finally { audio.FailRestore = false; final.Release.TrySetResult(); }
    }

    [Fact]
    public async Task DeferredEndpointRecoveryDoesNotHoldSerializationWhileNativeRetryRuns()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing with { Retry = TimeSpan.FromMinutes(1) });
        var settings = Settings(); settings.ForceReconnect = true;
        await StartStreamingAsync(controller, connection, Pod(), settings);
        var queued = NewSignal(); Task<bool>? passive = null;
        void QueueOnError(SessionSnapshot snapshot)
        {
            if (snapshot.State != PlaybackState.Error) return;
            var captured = controller.Capture(); passive = controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent); queued.TrySetResult();
        }
        controller.Changed += QueueOnError;
        try
        {
            var restores = audio.RestoreCount;
            connection.Emit(new { @event = "error", message = "synthetic retry", retryable = true });
            await queued.Task.WaitAsync(Deadline);
            Assert.NotNull(passive); Assert.False(await passive.WaitAsync(Deadline));
            var consumer = DeferredRecovery(controller);
            await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Connecting && snapshot.Message.Contains("reconnecting", StringComparison.OrdinalIgnoreCase));
            await controller.StopAsync().WaitAsync(Deadline); await consumer.WaitAsync(Deadline);
            Assert.Equal(restores + 3, audio.RestoreCount); Assert.Equal(1, factory.OpenCount); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        }
        finally { controller.Changed -= QueueOnError; }
    }

    [Fact]
    public async Task DisposeDrainsQueuedEndpointRecoveryBeforeDisposingSerialization()
    {
        var connection = new Connection(); var audio = new Audio(); var factory = new Factory(connection);
        var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var final = await HoldFinalRestoreAsync(controller, connection, audio);
        Task? disposal = null;
        try
        {
            var captured = controller.Capture(); var restores = audio.RestoreCount;
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            var consumer = DeferredRecovery(controller); disposal = controller.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            final.Release.TrySetResult(); await disposal.WaitAsync(Deadline); await consumer.WaitAsync(Deadline);
            Assert.Equal(restores + 1, audio.RestoreCount); Assert.Equal(1, factory.OpenCount);
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
        }
        finally { final.Release.TrySetResult(); if (disposal is null) await controller.DisposeAsync(); else await disposal.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task DisposeWaitsForAlreadyAdmittedDeferredRestore()
    {
        var connection = new Connection(); var audio = new Audio(); var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
        var final = await HoldFinalRestoreAsync(controller, connection, audio); var recovery = audio.PauseNextRestore(); Task? disposal = null;
        try
        {
            var captured = controller.Capture();
            Assert.False(await controller.TryRestoreAudioIfInactiveAsync(captured.Owner, captured.AutomaticIntent));
            var consumer = DeferredRecovery(controller); final.Release.TrySetResult(); await recovery.Entered.Task.WaitAsync(Deadline);
            disposal = controller.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            recovery.Release.TrySetResult(); await disposal.WaitAsync(Deadline); await consumer.WaitAsync(Deadline);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        }
        finally
        {
            final.Release.TrySetResult(); recovery.Release.TrySetResult();
            if (disposal is null) await controller.DisposeAsync(); else await disposal.WaitAsync(Deadline);
        }
    }

    private static async Task<Gate> HoldFinalRestoreAsync(SessionController controller, Connection connection, Audio audio)
    {
        await StartStreamingAsync(controller, connection, Pod());
        var first = audio.PauseNextRestore();
        Gate? final = null;
        try
        {
            connection.Emit(new { @event = "error", message = "synthetic terminal failure", retryable = false });
            await first.Entered.Task.WaitAsync(Deadline);
            final = audio.PauseNextRestore(); first.Release.TrySetResult(); await final.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(PlaybackState.Error, controller.Snapshot.State); return final;
        }
        catch { final?.Release.TrySetResult(); throw; }
        finally { first.Release.TrySetResult(); }
    }
    private static Task DeferredRecovery(SessionController controller)
        => (Task)typeof(SessionController).GetField("_endpointRecoveryTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleGuardedReceiverEditDoesNotRestoreNewOwnersSavedMuteBit(bool initiallyMuted)
    {
        var first = new Connection(); var second = new Connection(); var audio = new Audio(initiallyMuted);
        await using var controller = TestSessionController.Create(new Factory(first, second), audio, timing: Timing);
        var settings = Settings(); settings.MuteWhileStreaming = true;
        await StartStreamingAsync(controller, first, Pod(), settings);
        var stale = controller.Capture();
        await StartStreamingAsync(controller, second, Pod("b"), settings);
        var restores = audio.RestoreCount;

        Assert.False(await controller.TryUpdateReceiverAsync(Pod("stale"), settings, stale));

        Assert.True(audio.Muted);
        Assert.Equal(restores, audio.RestoreCount);
        Assert.False(second.Disposed);
        await controller.StopAsync();
        Assert.Equal(initiallyMuted, audio.Muted);
    }

    [Fact]
    public async Task AtomicCaptureIncludesCurrentAuthorityWhileOldSnapshotAwaitsCleanup()
    {
        var connection = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var before = controller.Capture(); var gate = audio.PauseNextRestore(); Task? stop = null;
        try
        {
            stop = controller.StopAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            var pending = controller.Capture();
            Assert.Same(before.Snapshot, pending.Snapshot);
            Assert.NotEqual(before.Owner, pending.Owner);
            Assert.NotEqual(pending.Snapshot.Generation, pending.Owner);
            Assert.NotEqual(before.AutomaticIntent, pending.AutomaticIntent);
            gate.Release.TrySetResult(); await stop.WaitAsync(Deadline);
            Assert.Equal(controller.Capture().Owner, controller.Snapshot.Generation);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        }
        finally { gate.Release.TrySetResult(); if (stop is not null) await stop.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedReceiverEditRejectsOfflineAndIncompleteNewTransports(bool incomplete)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartStreamingAsync(controller, connection, Pod());
        var captured = controller.Capture(); var commands = connection.Commands.Count;
        var unavailable = incomplete ? Pod("stereo:partial") with { Members = [Pod()] } : Pod() with { Online = false };
        Assert.False(await controller.TryUpdateReceiverAsync(unavailable, Settings(), captured));
        Assert.Same(captured.Snapshot, controller.Snapshot);
        Assert.Equal(commands, connection.Commands.Count);
        Assert.False(connection.Disposed); Assert.Equal(1, factory.OpenCount);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("incomplete")]
    [InlineData("same-identity")]
    [InlineData("same-address")]
    public async Task AutomaticSoftwareAdmissionCannotStartUnavailableOrDuplicateMembers(string invalid)
    {
        var (receiver, settings) = SoftwareGroup();
        receiver = invalid switch
        {
            "offline" => receiver with { Online = false },
            "incomplete" => receiver with { Members = [receiver.Members[0]] },
            "same-identity" => receiver with { Members = [receiver.Members[0], receiver.Members[1] with { DeviceId = receiver.Members[0].DeviceId }] },
            _ => receiver with { Members = [receiver.Members[0], receiver.Members[1] with { Address = receiver.Members[0].Address }] }
        };
        var factory = new Factory(); await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        if (invalid is "offline" or "incomplete")
            Assert.False(await controller.TryStartAutomaticAsync(receiver, settings, captured.Owner, captured.AutomaticIntent));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TryStartAutomaticAsync(receiver, settings, captured.Owner, captured.AutomaticIntent));
        Assert.Same(captured.Snapshot, controller.Snapshot); Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededSoftwareRemovalOrInvalidMembershipCannotCommitAfterCleanup(bool invalidMembership)
    {
        var (receiver, settings) = SoftwareGroup(); var connection = new Connection(); var audio = new Audio();
        await using var controller = TestSessionController.Create(new Factory(connection), audio, timing: Timing);
        await StartStreamingAsync(controller, connection, receiver, settings);
        var captured = controller.Capture(); var changed = settings.Clone();
        if (invalidMembership) changed.SoftwareGroups[0].RightId = "missing-member";
        else changed.SoftwareGroups.Clear();
        var gate = audio.PauseNextRestore(); Task<bool>? update = null;
        try
        {
            update = controller.TryUpdateSettingsAsync(changed, captured);
            await gate.Entered.Task.WaitAsync(Deadline);
            controller.InvalidateAutomaticIntent();
            gate.Release.TrySetResult(); Assert.False(await update.WaitAsync(Deadline));
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
            Assert.Null(controller.Snapshot.Receiver); Assert.True(connection.Disposed);
            Assert.Single(connection.Commands, command => command.Name == "start");
        }
        finally { gate.Release.TrySetResult(); if (update is not null) await update.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("application")]
    [InlineData("endpoint")]
    public async Task DefaultEndpointWorkCannotRestartApplicationOrExplicitEndpointCapture(string mode)
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var settings = Settings(); settings.CaptureMode = mode; settings.MuteWhileStreaming = true;
        settings.CaptureEndpoint = "synthetic-capture-endpoint";
        settings.CaptureApplicationPath = "synthetic-player.exe";
        settings.CaptureApplicationInstance = new(settings.CaptureApplicationPath, "Synthetic player", 42, 123);
        var captured = controller.Capture();
        Assert.True(await controller.TryStartAutomaticAsync(Pod(), settings, captured.Owner, captured.AutomaticIntent));
        await connection.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
        var owned = controller.Capture(); var commands = connection.Commands.Count;
        Assert.False(await controller.TryRestartForEndpointAsync(settings, owned.Owner, owned.AutomaticIntent));
        Assert.Same(owned.Snapshot, controller.Snapshot); Assert.Equal(commands, connection.Commands.Count);
        Assert.False(connection.Disposed); Assert.Equal(1, factory.OpenCount);
        var start = connection.Commands.Single(command => command.Name == "start").Parameters;
        Assert.Equal(mode == "application" ? "application" : "loopback", start.Text("source"));
        if (mode == "application")
        {
            Assert.Equal("synthetic-player.exe", start.Text("capture_application_path"));
            Assert.Equal(42, start.Integer("capture_process_id")); Assert.Equal(123, start.Integer("capture_process_started_at"));
            Assert.Equal(JsonValueKind.Null, start.GetProperty("capture_endpoint").ValueKind);
            Assert.Equal(0, audio.MuteCount);
        }
        else Assert.Equal("synthetic-capture-endpoint", start.Text("capture_endpoint"));
    }

    [Fact]
    public async Task AutomaticSoftwareSessionRetainsProfilesAndMemberControlsAcrossPassiveMigrationAndRetry()
    {
        var (receiver, settings) = SoftwareGroup(); var first = new Connection(); var second = new Connection();
        var factory = new Factory(first, second); await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var captured = controller.Capture();
        Assert.True(await controller.TryStartAutomaticAsync(receiver, settings, captured.Owner, captured.AutomaticIntent));
        await first.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
        var owned = controller.Capture();
        var start = first.Commands.Single(command => command.Name == "start").Parameters;
        Assert.Equal("software_stereo", start.Text("group_mode"));
        Assert.Equal("left", start.GetProperty("peers")[0].GetProperty("profile").Text("channel"));
        Assert.Equal("alac", start.GetProperty("peers")[0].GetProperty("profile").Text("codec"));
        Assert.Equal(-80, start.GetProperty("peers")[0].GetProperty("profile").Integer("sync_offset_ms"));
        Assert.Equal("pcm", start.GetProperty("peers")[1].GetProperty("profile").Text("codec"));
        first.Emit(new { @event = "device_volume", host = "192.0.2.10", volume = 40, sequence = 0, status = "confirmed", available = true });
        await SnapshotAsync(controller, snapshot => snapshot.MemberVolumes?.GetValueOrDefault("a")?.Actual == 40);
        var migrated = receiver with { Members = [receiver.Members[0] with { Id = "new-a", Address = "192.0.2.30", Features = 42 }, receiver.Members[1]] };
        await controller.RefreshReceiverAsync(migrated, owned.Snapshot, new Dictionary<string, string> { ["a"] = "new-a" });
        Assert.Equal(owned.Owner, controller.Capture().Owner); Assert.Equal(1, factory.OpenCount);
        Assert.Equal("192.0.2.10", controller.Snapshot.Receiver!.Members[0].Address);
        Assert.Null(controller.Snapshot.Receiver.Members[0].Features);
        Assert.Equal(40, controller.Snapshot.MemberVolumes!["new-a"].Actual);
        var edit = controller.SetDeviceVolumeAsync("new-a", 55);
        await first.VolumeSent.Task.WaitAsync(Deadline);
        var volume = first.Commands.Single(command => command.Name == "set_device_volume").Parameters;
        Assert.Equal("192.0.2.10", volume.Text("host"));
        first.Emit(new { @event = "device_volume", host = "192.0.2.10", volume = 55, sequence = volume.Integer("sequence"), status = "confirmed", available = true });
        await edit.WaitAsync(Deadline);
        first.Emit(new { @event = "error", code = "peer_closed", channel = "events", message = "synthetic software member fault", retryable = true });
        await second.Started.Task.WaitAsync(Deadline);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Diagnostics?.ReconnectCount == 1);
        Assert.Equal(owned.Owner, controller.Capture().Owner); Assert.NotEqual(first.Session, second.Session);
        var retry = second.Commands.Single(command => command.Name == "start").Parameters;
        Assert.Equal("192.0.2.30", retry.GetProperty("peers")[0].Text("host"));
        Assert.Equal("alac", retry.GetProperty("peers")[0].GetProperty("profile").Text("codec"));
        Assert.Equal("left", retry.GetProperty("peers")[0].GetProperty("profile").Text("channel"));
        Assert.Equal(-80, retry.GetProperty("peers")[0].GetProperty("profile").Integer("sync_offset_ms"));
        Assert.Equal("new-a", retry.GetProperty("peers")[0].Text("receiver_id"));
        Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed);
    }

    private static (Receiver Receiver, AppSettings Settings) SoftwareGroup()
    {
        var settings = Settings();
        settings.SoftwareGroups.Add(new() { Id = "software:synthetic", LeftId = "a", RightId = "b", LeftOffsetMs = -80, RightOffsetMs = 25 });
        settings.Options("a").Codec = "alac"; settings.Options("a").Transport = "buffered";
        settings.Options("b").Codec = "pcm"; settings.Options("b").Timing = "ntp";
        return (SoftwareStereo.Resolve([Pod("a") with { DeviceId = "synthetic-a" }, Pod("b") with { Address = "192.0.2.11", DeviceId = "synthetic-b" }], settings).Single(), settings);
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
        public int DisposeFailures { get; set; }
        public int DisposeCalls { get; private set; }
        public Action<int>? BeforeDispose { get; set; }
        public string Session => _session;
        public bool Disposed => _disposed;
        public ConcurrentQueue<Command> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource PairSent { get; } = NewSignal();
        public TaskCompletionSource VolumeSent { get; } = NewSignal();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue(new(command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            switch (command)
            {
                case "hello":
                    _session = session;
                    Emit(new { @event = "hello", capabilities = new[] { "receiver_ownership_guard", "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "member_volume", "receiver_password", "application_capture" } });
                    break;
                case "start":
                    _session = session; Started.TrySetResult();
                    if (AutoStreaming) Emit(new { @event = "streaming" });
                    break;
                case "pair":
                    _session = session; PairSent.TrySetResult();
                    if (RequestPin) Emit(new { @event = "pin_required" });
                    break;
                case "stop": _events.Writer.TryComplete(); break;
                case "set_device_volume": VolumeSent.TrySetResult(); break;
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
            var attempt = ++DisposeCalls;
            BeforeDispose?.Invoke(attempt);
            if (attempt <= DisposeFailures)
                throw new EngineShutdownException(this, new IOException("synthetic engine shutdown failure"));
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
        private int _muteCount;
        public bool Muted { get { lock (_sync) return _muted; } }
        public int RestoreCount => Volatile.Read(ref _restoreCount);
        public int MuteCount => Volatile.Read(ref _muteCount);
        public bool FailRestore { get; set; }
        private TaskCompletionSource? _observedRestore;
        public TaskCompletionSource ObserveNextRestore() => _observedRestore = NewSignal();
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("fake-endpoint");
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId)
        {
            Interlocked.Increment(ref _muteCount);
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
            if (FailRestore) throw new IOException("synthetic endpoint restore failure");
            lock (_sync)
            {
                if (_savedMute is not { } original) return;
                _muted = original; _savedMute = null;
            }
            _observedRestore?.TrySetResult();
        }
    }
}
