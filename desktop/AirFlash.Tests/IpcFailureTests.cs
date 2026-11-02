using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class IpcFailureTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static readonly SessionTiming Timing = new(Limit, Limit, Limit, TimeSpan.Zero);
    private const string Rejection = "{\"version\":1,\"id\":\"\",\"session_id\":\"\",\"event\":\"error\",\"code\":\"command_error\",\"channel\":\"ipc\",\"host\":null,\"retryable\":false,\"message\":\"synthetic command rejection\"}";
    private static AppSettings Settings() => new() { ForceReconnect = true, MaxReconnectAttempts = 1, MuteWhileStreaming = true };
    private static Receiver Receiver() => new("synthetic-ipc", "Synthetic IPC", "192.0.2.10", 7000);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("connecting", false)] [InlineData("connecting", true)]
    [InlineData("streaming", false)] [InlineData("streaming", true)]
    [InlineData("pairing", false)] [InlineData("pairing", true)]
    public async Task ExactProcessRejectionKeepsItsFaultAndTerminatesTheOwnedAttempt(string phase, bool originalMute)
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio(originalMute);
        await using var controller = new SessionController(factory, audio, timing: Timing);
        var fault = Signal(); var streaming = Signal();
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        if (phase == "pairing") await controller.PairAsync(Receiver(), Settings(), (_, _) => throw new InvalidOperationException("No PIN request is scripted"));
        else await controller.StartAsync(Receiver(), Settings());
        await connection.Started.Task.WaitAsync(Limit);
        await connection.Starting.Task.WaitAsync(Limit);
        if (phase == "streaming") { connection.Current("streaming"); await streaming.Task.WaitAsync(Limit); Assert.True(audio.Muted); }
        var restores = audio.Restores;
        connection.Raw(Rejection);
        await fault.Task.WaitAsync(Limit);
        var failed = controller.Snapshot;
        Assert.NotNull(failed.Diagnostics?.LastFault);
        var notice = failed.Diagnostics!.LastFault!;
        Assert.Equal("command_error", notice.Code); Assert.Equal("ipc", notice.Channel);
        Assert.Equal("", notice.Host); Assert.Equal(false, notice.Retryable);
        Assert.Equal("synthetic command rejection", notice.Message);
        Assert.Equal(notice.Detail, failed.Message);
        // Playback restores in its catch and outer finally; pairing has only the outer finally.
        await audio.WhenRestoredAtLeastAsync(restores + (phase == "pairing" ? 1 : 2)).WaitAsync(Limit);
        Assert.True(connection.Disposed); Assert.Contains("stop", connection.Commands);
        Assert.Equal(originalMute, audio.Muted); Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, failed.Diagnostics.ReconnectCount);
    }

    public static IEnumerable<object[]> NearMatches()
    {
        foreach (var (key, value) in new (string, object?)[]
        {
            ("version", 2), ("version", "1"), ("event", "equalizer_error"), ("code", "engine_error"),
            ("channel", "setup"), ("host", "192.0.2.11"), ("retryable", true), ("retryable", "false"),
            ("id", "other-request"), ("id", null), ("session_id", "stale-attempt"), ("session_id", null)
        })
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(Rejection)!; data[key] = value;
            yield return [JsonSerializer.Serialize(data)];
        }
        foreach (var key in new[] { "version", "event", "code", "channel", "host", "retryable", "id", "session_id" })
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(Rejection)!; data.Remove(key);
            yield return [JsonSerializer.Serialize(data)];
        }
    }
    [Theory] [MemberData(nameof(NearMatches))]
    public async Task NearMatchGlobalAndStaleEnvelopesRemainFiltered(string envelope)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var streaming = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings()); await connection.Starting.Task.WaitAsync(Limit);
        connection.Raw(envelope); connection.Current("streaming");
        await streaming.Task.WaitAsync(Limit);
        Assert.Null(controller.Snapshot.Diagnostics?.LastFault); Assert.Equal(1, factory.OpenCount);
        await controller.StopAsync(); Assert.True(connection.Disposed);
    }

    [Theory] [InlineData("[]")] [InlineData("null")] [InlineData("42")] [InlineData("\"text\"")] [InlineData("{")]
    public async Task MalformedOutputIsAControlledFinalIpcFailure(string raw)
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings()); await connection.Started.Task.WaitAsync(Limit); connection.Raw(raw);
        await fault.Task.WaitAsync(Limit);
        var notice = controller.Snapshot.Diagnostics!.LastFault!;
        Assert.Equal("protocol_error", notice.Code); Assert.Equal("ipc", notice.Channel); Assert.Equal(false, notice.Retryable);
        Assert.True(connection.Disposed); Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task CurrentStructuredPairingFailureKeepsClassificationWithoutPlayback()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.PairAsync(Receiver(), Settings(), (_, _) => throw new InvalidOperationException("No PIN requested"));
        await connection.Started.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "authentication_failed", channel = "authentication", host = "192.0.2.10", retryable = false, message = "synthetic verify rejected" });
        await fault.Task.WaitAsync(Limit);
        Assert.Equal("authentication_failed", controller.Snapshot.Diagnostics?.LastFault?.Code);
        Assert.Equal(false, controller.Snapshot.Diagnostics?.LastFault?.Retryable);
        Assert.DoesNotContain("start", connection.Commands); Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task APreemptedStreamGetsItsOwnStateAndNeverAutoReconnects()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio(false);
        await using var controller = new SessionController(factory, audio, timing: Timing);
        var streaming = Signal(); var preempted = Signal();
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Streaming) streaming.TrySetResult(); if (snapshot.State == PlaybackState.Preempted) preempted.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("streaming");
        await streaming.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "preempted", channel = "control", host = "192.0.2.10", retryable = false, message = "Another controller took over this receiver" });
        await preempted.Task.WaitAsync(Limit);
        var failed = controller.Snapshot;
        Assert.Equal(PlaybackState.Preempted, failed.State);
        Assert.Equal("preempted", failed.Diagnostics?.LastFault?.Code);
        Assert.Equal(false, failed.Diagnostics?.LastFault?.Retryable);
        Assert.Equal(L.Get("Another device took over this AirPlay session. Reconnect to claim it back."), failed.Message);
        Assert.True(connection.Disposed);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task PreemptedAtTheTakeoverLimitUsesTheYieldMessage()
    {
        var connection = new Connection(); var factory = new Factory(connection); var audio = new Audio(false);
        await using var controller = new SessionController(factory, audio, timing: Timing);
        var preempted = Signal();
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Preempted) preempted.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "preempted", channel = "control", host = "192.0.2.10", retryable = false, reason = "limit", message = "Another controller keeps taking over this receiver" });
        await preempted.Task.WaitAsync(Limit);
        var failed = controller.Snapshot;
        Assert.Equal(PlaybackState.Preempted, failed.State);
        Assert.Equal("limit", failed.Diagnostics?.LastFault?.Reason);
        Assert.Equal(L.Get("Another device keeps taking over this AirPlay session. AirFlash yielded and will not reconnect automatically."), failed.Message);
        Assert.True(connection.Disposed);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ClassicOnlySetupFailurePublishesTheCapabilityGateMessage()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        var receiver = Receiver() with { Model = "AirPort Express", Features = 0UL, SourceVersion = "220.68" };
        await controller.StartAsync(receiver, Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "protocol_error", channel = "setup", host = "192.0.2.10", retryable = false, message = "SETUP rejected" });
        await fault.Task.WaitAsync(Limit);
        Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
        Assert.Equal(L.Get("This device only supports classic AirPlay. AirFlash cannot stream to it yet."), controller.Snapshot.Message);
        Assert.Equal("protocol_error", controller.Snapshot.Diagnostics?.LastFault?.Code);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task AuthenticationFailurePublishesThePairingGateMessage()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "authentication_failed", channel = "authentication", host = "192.0.2.10", retryable = false, message = "synthetic verify rejected" });
        await fault.Task.WaitAsync(Limit);
        Assert.Equal(L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."), controller.Snapshot.Message);
        Assert.Equal("authentication_failed", controller.Snapshot.Diagnostics?.LastFault?.Code);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TlvTwoAuthenticationFailurePublishesAnImmediateHomeAccessHint(bool pairing)
    {
        var connection = new Connection(); var next = new Connection(); var factory = new Factory(connection, next);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        if (pairing) await controller.PairAsync(Receiver(), Settings(), (_, _) => throw new InvalidOperationException("No PIN requested"));
        else await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        const string detail = "pairing rejected (TLV error [2]): Caused by: rejected by accessory";
        connection.Current("error", new { code = "authentication_failed", channel = "authentication", host = "192.0.2.10", retryable = false, message = detail });
        await fault.Task.WaitAsync(Limit);

        Assert.Equal(L.Get("This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers."), controller.Snapshot.Message);
        Assert.Equal(L.Get("In the Home app, set speaker access to “Everyone,” then try again."), controller.Snapshot.ActionHint);
        Assert.Contains(detail, controller.Snapshot.Diagnostics?.LastFault?.Detail);
        Assert.Equal(detail, controller.Snapshot.Diagnostics?.LastFault?.Message);

        await controller.StopAsync();
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        Assert.Equal("", controller.Snapshot.ActionHint);
        Assert.Contains(detail, controller.Snapshot.Diagnostics?.LastFault?.Detail);

        var nextFault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) nextFault.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings());
        await next.Starting.Task.WaitAsync(Limit);
        Assert.Equal(PlaybackState.Connecting, controller.Snapshot.State);
        Assert.Equal("", controller.Snapshot.ActionHint);
        next.Current("streaming");
        next.Current("error", new { code = "authentication_failed", channel = "authentication", retryable = false, message = "pairing rejected (TLV error [3])" });
        await nextFault.Task.WaitAsync(Limit);
        Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
        Assert.Equal("", controller.Snapshot.ActionHint);
    }

    [Fact]
    public async Task TlvThreeAuthenticationFailureDoesNotPublishTheHomeAccessHint()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("error", new { code = "authentication_failed", channel = "authentication", host = "192.0.2.10", retryable = false, message = "pairing rejected (TLV error [3])" });
        await fault.Task.WaitAsync(Limit);

        Assert.Equal("", controller.Snapshot.ActionHint);
        Assert.Contains("TLV error [3]", controller.Snapshot.Diagnostics?.LastFault?.Detail);
    }

    [Fact]
    public async Task SocketFailureBeforeAConnectionExistsPublishesTheNetworkGateMessage()
    {
        var factory = new UnreachableFactory();
        await using var controller = new SessionController(factory, new Audio(false), timing: Timing);
        var fault = Signal(); controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.StartAsync(Receiver(), new AppSettings { ForceReconnect = false, MaxReconnectAttempts = 1 });
        await fault.Task.WaitAsync(Limit);
        Assert.Equal(L.Get("Could not reach the receiver over the network. Check that it is powered on and on the same network as this PC."), controller.Snapshot.Message);
        Assert.Equal("network_error", controller.Snapshot.Diagnostics?.LastFault?.Code);
        Assert.Equal("desktop", controller.Snapshot.Diagnostics?.LastFault?.Channel);
        Assert.Equal(1, factory.OpenCount);
    }

    private sealed class UnreachableFactory : IEngineFactory
    {
        public int OpenCount;
        public IEngineConnection Open() { ++OpenCount; throw new SocketException((int)SocketError.ConnectionRefused); }
    }

    [Fact]
    public async Task LifetimeCancellationRemainsIdleAndDisposesItsConnection()
    {
        var connection = new Connection(); var factory = new Factory(connection);
        await using var controller = new SessionController(factory, new Audio(true), timing: Timing);
        await controller.StartAsync(Receiver(), Settings()); await connection.Started.Task.WaitAsync(Limit);
        await connection.Reading.Task.WaitAsync(Limit);
        await controller.StopAsync().WaitAsync(Limit);
        connection.Raw(Rejection);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Diagnostics?.LastFault);
        Assert.True(connection.Disposed); Assert.Equal(1, factory.OpenCount);
    }

    [Theory] [InlineData(Rejection)] [InlineData("[]")] [InlineData("{")]
    public async Task StartupKeepsTheIpcCauseAndDisposesTheProbe(string raw)
    {
        var connection = new Connection(); connection.Raw(raw);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => EngineHandshake.HelloAsync(new Factory(connection), limit: Limit));
        Assert.Contains("ipc", error.Message);
        if (raw == Rejection) Assert.Contains("synthetic command rejection", error.Message);
        Assert.True(connection.Disposed);
    }

    private sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _connections = new(connections);
        public int OpenCount;
        public IEngineConnection Open() { Interlocked.Increment(ref OpenCount); return _connections.TryDequeue(out var connection) ? connection : throw new IOException("unexpected retry"); }
    }
    private sealed class Connection : IEngineConnection
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        public readonly ConcurrentQueue<string> Commands = new();
        public readonly TaskCompletionSource Started = Signal(), Starting = Signal(), Reading = Signal();
        public bool Disposed;
        private string _session = "";
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Commands.Enqueue(command);
            if (command == "hello")
            {
                _session = session;
                Current("hello", new { capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_ownership_guard", "member_volume", "receiver_password", "application_capture" } });
                Started.TrySetResult();
            }
            if (command is "start" or "pair") { _session = session; Starting.TrySetResult(); }
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public void Raw(string raw) => _events.Writer.TryWrite(raw);
        public void Current(string name, object? fields = null)
        {
            var data = fields is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fields))!;
            data["version"] = 1; data["session_id"] = _session; data["event"] = name; Raw(JsonSerializer.Serialize(data));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            Reading.TrySetResult();
            try { using var json = JsonDocument.Parse(await _events.Reader.ReadAsync(cancellation)); return json.RootElement.Clone(); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { Disposed = true; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Audio(bool original) : IAudioService
    {
        private readonly bool _original = original;
        private bool _owned;
        private readonly object _sync = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        public bool Muted = original;
        public int Restores;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId) { _owned = true; Muted = true; return Task.CompletedTask; }
        public Task RestoreAsync()
        {
            lock (_sync)
            {
                if (_owned) { Muted = _original; _owned = false; }
                ++Restores;
                foreach (var waiter in _waiters.Where(w => w.Count <= Restores).ToArray()) { waiter.Signal.TrySetResult(); _waiters.Remove(waiter); }
            }
            return Task.CompletedTask;
        }
        public Task WhenRestoredAtLeastAsync(int count)
        {
            lock (_sync) { if (Restores >= count) return Task.CompletedTask; var signal = Signal(); _waiters.Add((count, signal)); return signal.Task; }
        }
    }
}
