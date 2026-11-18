using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class SessionSnapshotPublicationTests
{
    [Fact]
    public async Task Selected_rate_snapshot_is_published_before_waiting_for_streaming()
    {
        var connection = new Connection(); var snapshots = new ConcurrentQueue<SessionSnapshot>();
        await using var controller = TestSessionController.Create(new Factory(connection), new Audio());
        controller.Changed += snapshots.Enqueue;
        await controller.StartAsync(new("synthetic-publication", "Synthetic publication", "192.0.2.55"), new());
        await connection.StandbySent.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var current = controller.Snapshot;
        Assert.Equal(PlaybackState.Connecting, current.State); Assert.Equal(44100, current.StreamRate);
        Assert.Contains(snapshots, published => ReferenceEquals(published, current));
        Assert.True(current.IsActive);
    }
    private sealed class Factory(Connection connection) : IEngineFactory { public IEngineConnection Open() => connection; }
    private sealed class Connection : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public readonly TaskCompletionSource StandbySent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            if (command == "hello") _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = "hello", capabilities = new[] { "receiver_ownership_guard", "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release" } }));
            if (command == "set_standby") StandbySent.TrySetResult();
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        { try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Audio : IAudioService
    {
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>(null);
        public Task MuteAsync(string? endpointId) => Task.CompletedTask;
        public Task RestoreAsync() => Task.CompletedTask;
    }
}
