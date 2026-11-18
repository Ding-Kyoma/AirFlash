using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

/// <summary>A scripted engine for SessionController tests that need the
/// parameters of the commands the controller sends.</summary>
internal static class SessionTestEngine
{
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    internal static readonly SessionTiming Timing = new(Limit, Limit, Limit, TimeSpan.Zero);
    internal static AppSettings Settings() => new() { ForceReconnect = false, MaxReconnectAttempts = 1 };
    internal static Receiver Receiver() => new("synthetic-session", "Synthetic", "192.0.2.10", 7000);

    /// <summary>Start a session and return once the streaming event has been fully
    /// handled: the reader handles events in order, so a later warning showing up in
    /// the diagnostics proves the streaming handler (and its Now Playing send) is done.</summary>
    internal static async Task StreamAsync(SessionController controller, SessionTestEngine.Connection connection)
    {
        await controller.StartAsync(Receiver(), Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("streaming");
        connection.Current("warning", new { code = "synthetic_marker", channel = "test", host = "192.0.2.10", recovered = false, message = "marker" });
        var deadline = DateTime.UtcNow + Limit;
        while (controller.Snapshot.Diagnostics?.Warnings?.Any(w => w.Code == "synthetic_marker") != true)
        {
            Assert.True(DateTime.UtcNow < deadline, "the streaming event was not handled");
            await Task.Delay(10);
        }
        Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
    }

    internal sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _connections = new(connections);
        public IEngineConnection Open() => _connections.TryDequeue(out var connection) ? connection : throw new IOException("unexpected retry");
    }
    internal sealed class Connection : ICorrelatedEngineConnection
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<(string Command, JsonElement Parameters)> _commands = new();
        private readonly ConcurrentQueue<(string Command, string RequestId)> _requests = new();
        public readonly TaskCompletionSource Starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _session = "";
        /// <summary>What hello advertises; defaults to what the real engine reports.</summary>
        public string[] Capabilities { get; init; } = ["receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_ownership_guard", "now_playing", "diagnostics"];
        public int Count(string command) => _commands.Count(c => c.Command == command);
        public JsonElement Last(string command) => _commands.Last(c => c.Command == command).Parameters;
        /// <summary>Extra scripted replies, called after each command is recorded.</summary>
        public Action<Connection, string>? OnCommand { get; init; }
        /// <summary>Pause a selected command before it reaches the fake engine.</summary>
        public Func<string, Task>? BeforeCommandAsync { get; init; }
        public Func<string, Task>? AfterCommandAsync { get; init; }
        public string LastRequestId(string command) => _requests.Last(request => request.Command == command).RequestId;
        public async Task WaitForAsync(string command, int count = 1)
        {
            var deadline = DateTime.UtcNow + Limit;
            while (Count(command) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, command + " was not sent");
                await Task.Delay(10);
            }
        }
        public string[] NowPlayingTitles() => _commands.Where(c => c.Command == "set_now_playing").Select(c => c.Parameters.GetProperty("title").GetString()!).ToArray();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
            => SendCorrelatedAsync(Guid.NewGuid().ToString("N"), session, command, parameters, cancellation);
        public async Task SendCorrelatedAsync(string requestId, string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (BeforeCommandAsync is { } before) await before(command).WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            _session = session;
            _requests.Enqueue((command, requestId));
            _commands.Enqueue((command, JsonSerializer.SerializeToElement(parameters)));
            if (command == "hello")
            {
                Current("hello", new { capabilities = Capabilities });
            }
            if (command == "start") Starting.TrySetResult();
            if (command == "stop") _events.Writer.TryComplete();
            OnCommand?.Invoke(this, command);
            if (AfterCommandAsync is { } after) await after(command).WaitAsync(cancellation).ConfigureAwait(false);
        }
        public void Current(string name, object? fields = null)
        {
            var data = fields is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fields))!;
            data["version"] = 1; data["session_id"] = _session; data["event"] = name;
            _events.Writer.TryWrite(JsonSerializer.Serialize(data));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { using var json = JsonDocument.Parse(await _events.Reader.ReadAsync(cancellation)); return json.RootElement.Clone(); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    internal sealed class Audio : IAudioService
    {
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId) => Task.CompletedTask;
        public Task RestoreAsync() => Task.CompletedTask;
    }
}
