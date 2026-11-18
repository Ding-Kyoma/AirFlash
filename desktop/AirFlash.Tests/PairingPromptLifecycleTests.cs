using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class PairingPromptLifecycleTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static readonly SessionTiming Timing = new(Limit, Limit, TimeSpan.FromSeconds(30), TimeSpan.Zero);
    private const string Rejection = "{\"version\":1,\"id\":\"\",\"session_id\":\"\",\"event\":\"error\",\"code\":\"command_error\",\"channel\":\"ipc\",\"host\":null,\"retryable\":false,\"message\":\"synthetic open-prompt rejection\"}";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Receiver Pod(bool password = false) => new("synthetic-prompt", "Synthetic prompt", "192.0.2.15") { RequiresPassword = password };
    private static AppSettings Settings() => new() { ForceReconnect = true, MaxReconnectAttempts = 1 };

    [Theory]
    [InlineData(false, "ipc")]
    [InlineData(true, "ipc")]
    [InlineData(false, "scoped")]
    [InlineData(true, "scoped")]
    [InlineData(false, "eof")]
    [InlineData(true, "eof")]
    [InlineData(false, "malformed")]
    [InlineData(false, "stopped")]
    public async Task Terminal_event_cancels_and_joins_open_input_before_failure(bool password, string terminal)
    {
        var factory = new Factory(); var prompt = new Prompt(); var audio = new Audio();
        var controller = TestSessionController.Create(factory, audio, timing: Timing);
        var failed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += snapshot =>
        {
            if (snapshot.State == PlaybackState.Error)
            {
                Assert.True(prompt.Settled.Task.IsCompleted, "Failure must not leave an orphan input dialog.");
                failed.TrySetResult(snapshot);
            }
        };
        try
        {
            await controller.PairAsync(Pod(password), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            var connection = factory.Connections.Single();
            switch (terminal)
            {
                case "ipc": connection.Raw(Rejection); break;
                case "scoped": connection.Emit("error", new { code = "authentication_failed", channel = "authentication", retryable = false, message = "synthetic authentication rejection" }); break;
                case "eof": connection.End(); break;
                case "malformed": connection.Raw("{"); break;
                case "stopped": connection.Emit("stopped"); break;
            }
            var snapshot = await failed.Task.WaitAsync(Limit);
            await connection.Disposed.Task.WaitAsync(Limit);
            Assert.True(prompt.Cancelled.Task.IsCompleted);
            Assert.Equal(PlaybackState.Error, snapshot.State);
            Assert.Single(factory.Connections);
            Assert.DoesNotContain(connection.Commands, command => command.Command is "pair_pin" or "start");
            Assert.Equal(1, connection.MaximumReaders);
            Assert.Equal(0, connection.ActiveReaders);
            Assert.Equal(0, audio.Mutes);
            Assert.Equal(0, snapshot.Diagnostics!.ReconnectCount);
            if (terminal == "ipc")
            {
                Assert.Equal("command_error", snapshot.Diagnostics.LastFault!.Code);
                Assert.Equal("ipc", snapshot.Diagnostics.LastFault.Channel);
                Assert.Equal(false, snapshot.Diagnostics.LastFault.Retryable);
                Assert.Equal("synthetic open-prompt rejection", snapshot.Diagnostics.LastFault.Message);
            }
            if (terminal == "scoped") Assert.Equal("authentication_failed", snapshot.Diagnostics.LastFault!.Code);
            if (terminal == "malformed") Assert.Equal("protocol_error", snapshot.Diagnostics.LastFault!.Code);
            if (terminal == "eof") Assert.Contains("exited", snapshot.Message);
        }
        finally
        {
            prompt.Result.TrySetResult(null);
            await controller.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Stop_cancels_and_joins_open_prompt_and_only_event_reader()
    {
        var factory = new Factory(); var prompt = new Prompt();
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        try
        {
            await controller.PairAsync(Pod(), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            await controller.StopAsync().WaitAsync(Limit);
            Assert.True(prompt.Cancelled.Task.IsCompleted); Assert.True(prompt.Settled.Task.IsCompleted);
            Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Diagnostics?.LastFault);
            var connection = Assert.Single(factory.Connections);
            Assert.True(connection.Disposed.Task.IsCompleted); Assert.Equal(0, connection.ActiveReaders);
            Assert.DoesNotContain(connection.Commands, command => command.Command is "pair_pin" or "start");
        }
        finally { prompt.Result.TrySetResult(null); }
    }

    [Theory]
    [InlineData(false, "1234")]
    [InlineData(true, "  exact-password-123  ")]
    public async Task Prompt_win_preserves_pending_reader_and_submits_exact_value(bool password, string value)
    {
        var factory = new Factory(); var prompt = new Prompt();
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var streaming = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        try
        {
            await controller.PairAsync(Pod(password), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            var pairing = factory.Connections.Single();
            // Neither stale ordinary failures nor near-match global envelopes own this prompt.
            pairing.Emit("error", new { message = "stale error" }, "older-session");
            pairing.Raw(Rejection.Replace("\"retryable\":false", "\"retryable\":true", StringComparison.Ordinal));
            pairing.Emit("warning", new { message = "ordinary pairing progress" });
            prompt.Result.TrySetResult(value);
            await streaming.Task.WaitAsync(Limit);
            Assert.Equal(password, prompt.Receiver!.RequiresPassword);
            var submitted = Assert.Single(pairing.Commands, command => command.Command == "pair_pin");
            Assert.Equal(value, submitted.Parameters.GetProperty("pin").GetString());
            Assert.Equal(1, pairing.MaximumReaders); Assert.Equal(0, pairing.ActiveReaders);
            Assert.True(pairing.Disposed.Task.IsCompleted); Assert.Equal(2, factory.Connections.Count);
            Assert.Null(controller.Snapshot.Diagnostics?.LastFault);
        }
        finally { prompt.Result.TrySetResult(null); }
    }

    [Fact]
    public async Task Paired_while_input_is_open_closes_prompt_without_late_submission()
    {
        var factory = new Factory(); var prompt = new Prompt();
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var streaming = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        try
        {
            await controller.PairAsync(Pod(), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            var pairing = factory.Connections.Single(); pairing.Emit("paired");
            await streaming.Task.WaitAsync(Limit);
            Assert.True(prompt.Cancelled.Task.IsCompleted); Assert.True(prompt.Settled.Task.IsCompleted);
            Assert.DoesNotContain(pairing.Commands, command => command.Command == "pair_pin");
        }
        finally { prompt.Result.TrySetResult(null); }
    }

    [Fact]
    public async Task Terminal_failure_waits_for_prompt_cleanup_and_keeps_its_original_cause()
    {
        var factory = new Factory(); var prompt = new Prompt { HoldCancellation = true, ThrowDuringCancellation = true };
        var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var failed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) failed.TrySetResult(snapshot); };
        try
        {
            await controller.PairAsync(Pod(), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            var connection = factory.Connections.Single(); connection.Raw(Rejection);
            await prompt.Cancelled.Task.WaitAsync(Limit);
            Assert.False(failed.Task.IsCompleted); Assert.False(connection.Disposed.Task.IsCompleted);
            prompt.AllowSettlement.TrySetResult();
            var snapshot = await failed.Task.WaitAsync(Limit);
            Assert.True(prompt.Settled.Task.IsCompleted);
            Assert.Equal("command_error", snapshot.Diagnostics!.LastFault!.Code);
            Assert.Equal("synthetic open-prompt rejection", snapshot.Diagnostics.LastFault.Message);
            Assert.DoesNotContain(connection.Commands, command => command.Command == "pair_pin");
        }
        finally
        {
            prompt.AllowSettlement.TrySetResult(); prompt.Result.TrySetResult(null);
            await controller.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Already_observed_terminal_wins_when_valid_input_completes_in_the_same_callback()
    {
        // Inline channel continuations let the one pending reader finish all
        // the way through ReadEventAsync before the input callback returns.
        // Both tasks are therefore already complete when the controller races
        // them; no scheduler delay selects the expected winner.
        var factory = new Factory(synchronousEvents: true);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var failed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) failed.TrySetResult(snapshot); };
        await controller.PairAsync(Pod(), Settings(), (_, _) =>
        {
            var connection = factory.Connections.Single();
            Assert.Equal(1, connection.ActiveReaders);
            connection.Raw(Rejection);
            Assert.Equal(0, connection.ActiveReaders);
            return Task.FromResult<string?>("1234");
        });
        var snapshot = await failed.Task.WaitAsync(Limit);
        var pairing = Assert.Single(factory.Connections);
        await pairing.Disposed.Task.WaitAsync(Limit);
        Assert.Equal("command_error", snapshot.Diagnostics!.LastFault!.Code);
        Assert.Equal("ipc", snapshot.Diagnostics.LastFault.Channel);
        Assert.Equal(false, snapshot.Diagnostics.LastFault.Retryable);
        Assert.Equal("synthetic open-prompt rejection", snapshot.Diagnostics.LastFault.Message);
        Assert.DoesNotContain(pairing.Commands, command => command.Command is "pair_pin" or "start");
        Assert.Equal(1, pairing.MaximumReaders);
    }

    [Fact]
    public async Task Input_timeout_settles_prompt_and_event_reader_without_submission()
    {
        var factory = new Factory(); var prompt = new Prompt();
        var clock = new ManualDeadlineClock(); var pinDeadline = TimeSpan.FromSeconds(1);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing with { Pin = pinDeadline }, clock: clock);
        var failed = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) failed.TrySetResult(); };
        try
        {
            await controller.PairAsync(Pod(), Settings(), prompt.RequestAsync);
            await prompt.Entered.Task.WaitAsync(Limit);
            Assert.False(failed.Task.IsCompleted);
            clock.WakeAfter(pinDeadline, expectedDue: pinDeadline);
            await failed.Task.WaitAsync(Limit);
            var connection = factory.Connections.Single(); await connection.Disposed.Task.WaitAsync(Limit);
            Assert.True(prompt.Settled.Task.IsCompleted); Assert.True(prompt.Cancelled.Task.IsCompleted);
            Assert.Equal(0, connection.ActiveReaders);
            Assert.DoesNotContain(connection.Commands, command => command.Command is "pair_pin" or "start");
        }
        finally { prompt.Result.TrySetResult(null); }
    }

    [Fact]
    public async Task Leader_first_member_pairing_still_opens_one_process_per_member_then_playback()
    {
        var factory = new Factory(); var requested = new ConcurrentQueue<string>();
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        var streaming = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        var left = Pod() with { Id = "synthetic-left", IsLeader = true };
        var right = Pod(true) with { Id = "synthetic-right", Address = "192.0.2.16" };
        await controller.PairAsync(new("stereo:synthetic-prompt", "Synthetic pair", "") { Members = [right, left] }, Settings(), (receiver, _) =>
        {
            requested.Enqueue(receiver.Id); return Task.FromResult<string?>(receiver.RequiresPassword ? " password " : "1234");
        });
        await streaming.Task.WaitAsync(Limit);
        Assert.Equal(new[] { left.Id, right.Id }, requested);
        var connections = factory.Connections.ToArray(); Assert.Equal(3, connections.Length);
        foreach (var pairing in connections.Take(2))
        {
            Assert.True(pairing.Disposed.Task.IsCompleted); Assert.Equal(1, pairing.MaximumReaders); Assert.Equal(0, pairing.ActiveReaders);
            Assert.Single(pairing.Commands, command => command.Command == "pair_pin");
        }
        Assert.Single(connections[2].Commands, command => command.Command == "start");
    }

    private sealed class Prompt
    {
        public readonly TaskCompletionSource Entered = Signal(), Cancelled = Signal(), Settled = Signal();
        public readonly TaskCompletionSource AllowSettlement = Signal();
        public readonly TaskCompletionSource<string?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Receiver? Receiver;
        public bool HoldCancellation, ThrowDuringCancellation;
        public async Task<string?> RequestAsync(Receiver receiver, CancellationToken cancellation)
        {
            Receiver = receiver; Entered.TrySetResult();
            try { return await Result.Task.WaitAsync(cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                if (HoldCancellation) await AllowSettlement.Task;
                if (ThrowDuringCancellation) throw new IOException("synthetic prompt cleanup failure");
                throw;
            }
            finally { Settled.TrySetResult(); }
        }
    }
    private sealed class Factory(bool synchronousEvents = false) : IEngineFactory
    {
        public readonly ConcurrentQueue<Connection> Connections = new();
        public IEngineConnection Open() { var connection = new Connection(synchronousEvents); Connections.Enqueue(connection); return connection; }
    }
    private sealed class Connection(bool synchronousEvents) : IEngineConnection
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { AllowSynchronousContinuations = synchronousEvents });
        private string _session = "";
        private int _activeReaders, _maximumReaders;
        public int ActiveReaders => Volatile.Read(ref _activeReaders);
        public int MaximumReaders => Volatile.Read(ref _maximumReaders);
        public readonly ConcurrentQueue<(string Command, JsonElement Parameters)> Commands = new();
        public readonly TaskCompletionSource Disposed = Signal();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); _session = session;
            Commands.Enqueue((command, JsonSerializer.SerializeToElement(parameters)));
            if (command == "hello") Emit("hello", new { capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_password", "member_volume", "receiver_ownership_guard" } });
            if (command == "pair") Emit(JsonSerializer.SerializeToElement(parameters).GetProperty("password_required").GetBoolean() ? "password_required" : "pin_required");
            if (command == "pair_pin") Emit("paired");
            if (command == "start") Emit("streaming");
            if (command == "stop") End();
            return Task.CompletedTask;
        }
        public void Raw(string raw) => _events.Writer.TryWrite(raw);
        public void End() => _events.Writer.TryComplete();
        public void Emit(string name, object? values = null, string? session = null)
        {
            var fields = values is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(values))!;
            fields["version"] = 1; fields["session_id"] = session ?? _session; fields["event"] = name;
            Raw(JsonSerializer.Serialize(fields));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            var count = Interlocked.Increment(ref _activeReaders); InterlockedExtensionsMax(ref _maximumReaders, count);
            try { using var document = JsonDocument.Parse(await _events.Reader.ReadAsync(cancellation)); return document.RootElement.Clone(); }
            catch (ChannelClosedException) { return null; }
            finally { Interlocked.Decrement(ref _activeReaders); }
        }
        private static void InterlockedExtensionsMax(ref int location, int value)
        {
            int previous;
            do { previous = Volatile.Read(ref location); if (previous >= value) return; }
            while (Interlocked.CompareExchange(ref location, value, previous) != previous);
        }
        public ValueTask DisposeAsync() { End(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class Audio : IAudioService
    {
        public int Mutes;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>(null);
        public Task MuteAsync(string? endpointId) { ++Mutes; return Task.CompletedTask; }
        public Task RestoreAsync() => Task.CompletedTask;
    }
}
