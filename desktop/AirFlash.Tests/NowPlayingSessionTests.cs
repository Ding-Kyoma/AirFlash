using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NowPlayingSessionTests
{
    private static readonly TimeSpan Limit = SessionTestEngine.Limit;
    private static readonly SessionTiming Timing = SessionTestEngine.Timing;
    private static NowPlaying Track(string title) => new(title, "Artist", "Album", "", false, 240_000, 30_000);

    [Fact]
    public async Task A_value_published_before_the_stream_is_sent_when_it_starts()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: Timing);
        await controller.PublishNowPlayingAsync(Track("Before"));
        await SessionTestEngine.StreamAsync(controller, connection);
        Assert.Equal(["Before"], connection.NowPlayingTitles());
    }

    [Theory]
    [InlineData(true, 240_000, 95_000)]
    [InlineData(true, 80_000, 80_000)]
    [InlineData(false, 240_000, 30_000)]
    [InlineData(true, 0, 95_000)]
    public async Task Replay_projects_cached_progress_to_the_new_stream(bool playing, long durationMs, long expectedPosition)
    {
        var clock = new AdaptiveLatencyTests.ManualClock();
        var connection = new SessionTestEngine.Connection();
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: Timing, clock: clock);
        await controller.PublishNowPlayingAsync(Track("Before") with { Playing = playing, DurationMs = durationMs });
        clock.Advance(65);

        await SessionTestEngine.StreamAsync(controller, connection);

        Assert.Equal(["Before"], connection.NowPlayingTitles());
        Assert.Equal(expectedPosition, connection.Last("set_now_playing").GetProperty("position_ms").GetInt64());
    }

    [Fact]
    public async Task A_clear_while_resuming_reaches_the_existing_native_session()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: Timing);
        await SessionTestEngine.StreamAsync(controller, connection);
        await controller.PublishNowPlayingAsync(Track("Old"));
        var standby = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(SessionSnapshot snapshot)
        {
            if (snapshot.State == PlaybackState.Standby) standby.TrySetResult();
            if (snapshot.State == PlaybackState.Connecting) resuming.TrySetResult();
        }

        controller.Changed += Observe;
        try
        {
            connection.Current("standby", new { reason = "silence" });
            await standby.Task.WaitAsync(Limit);
            connection.Current("resuming");
            await resuming.Task.WaitAsync(Limit);

            await controller.ClearNowPlayingAsync().WaitAsync(Limit);

            Assert.Equal(PlaybackState.Connecting, controller.Snapshot.State);
            Assert.Equal(1, connection.Count("start"));
            Assert.Equal(["Old", ""], connection.NowPlayingTitles());
        }
        finally { controller.Changed -= Observe; }
    }

    [Fact]
    public async Task A_clear_does_not_get_overwritten_by_an_in_flight_streaming_replay()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var connection = new SessionTestEngine.Connection
        {
            BeforeCommandAsync = command =>
            {
                if (command != "set_now_playing" || Interlocked.Increment(ref writes) != 1) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: Timing);
        await controller.PublishNowPlayingAsync(Track("Old"));
        var stream = SessionTestEngine.StreamAsync(controller, connection);
        try
        {
            await entered.Task.WaitAsync(Limit);
            var clear = controller.ClearNowPlayingAsync();
            release.TrySetResult();

            await clear.WaitAsync(Limit);
            await stream.WaitAsync(Limit);
            Assert.Equal(["Old", ""], connection.NowPlayingTitles());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_cleared_source_empties_the_live_engine_and_is_not_shown_by_a_later_session()
    {
        var first = new SessionTestEngine.Connection(); var second = new SessionTestEngine.Connection();
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(first, second), new SessionTestEngine.Audio(), timing: Timing);
        await SessionTestEngine.StreamAsync(controller, first);
        await controller.PublishNowPlayingAsync(Track("Old"));
        // The engine sends nothing to the receiver for an empty value, so the
        // display is kept while the engine's copy is dropped.
        await controller.ClearNowPlayingAsync();
        Assert.Equal(["Old", ""], first.NowPlayingTitles());
        await controller.ClearNowPlayingAsync();
        Assert.Equal(2, first.NowPlayingTitles().Length);

        await controller.StopAsync().WaitAsync(Limit);
        await SessionTestEngine.StreamAsync(controller, second);
        Assert.Empty(second.NowPlayingTitles());
    }

    [Fact]
    public async Task A_source_cleared_while_idle_sends_no_command_and_cannot_seed_the_next_session()
    {
        var first = new SessionTestEngine.Connection(); var second = new SessionTestEngine.Connection();
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(first, second), new SessionTestEngine.Audio(), timing: Timing);
        await SessionTestEngine.StreamAsync(controller, first);
        await controller.PublishNowPlayingAsync(Track("Old"));
        await controller.StopAsync().WaitAsync(Limit);

        await controller.ClearNowPlayingAsync();
        Assert.Equal(["Old"], first.NowPlayingTitles());
        await SessionTestEngine.StreamAsync(controller, second);
        Assert.Empty(second.NowPlayingTitles());
    }

    [Fact]
    public async Task An_engine_without_the_capabilities_gets_neither_command()
    {
        var connection = new SessionTestEngine.Connection { Capabilities = ["receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_ownership_guard"] };
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: Timing);
        await controller.PublishNowPlayingAsync(Track("Before"));
        await SessionTestEngine.StreamAsync(controller, connection);
        await controller.PublishNowPlayingAsync(Track("During"));
        await controller.ClearNowPlayingAsync();
        Assert.Null(await controller.DiagnosticSnapshotAsync().WaitAsync(Limit));
        Assert.Equal(0, connection.Count("set_now_playing"));
        Assert.Equal(0, connection.Count("diag_summary"));
    }

    [Fact]
    public async Task Receiver_info_is_logged_with_its_model_and_firmware()
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var connection = new SessionTestEngine.Connection
        {
            OnCommand = (engine, command) =>
            {
                if (command == "start") engine.Current("peer_info", new { host = "192.0.2.10", model = "AudioAccessory5,1", source_version = "870.14.1" });
            }
        };
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), log: lines.Enqueue, timing: Timing);
        await SessionTestEngine.StreamAsync(controller, connection);
        Assert.Contains("Receiver info: host 192.0.2.10; model AudioAccessory5,1; source_version 870.14.1", lines);
    }
}
