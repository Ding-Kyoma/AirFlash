using AirFlash.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
namespace AirFlash.Tests;

public sealed class MuteRecoveryAdmissionTests
{
    [Theory]
    [InlineData(MuteRecoveryStatus.NotInitialized)]
    [InlineData(MuteRecoveryStatus.Pending)]
    [InlineData(MuteRecoveryStatus.Busy)]
    [InlineData(MuteRecoveryStatus.Unreadable)]
    [InlineData(MuteRecoveryStatus.Unrecognized)]
    public async Task Unresolved_recovery_rejects_start_and_pair_before_opening_an_engine(MuteRecoveryStatus status)
    {
        var audio = new RecoveryAudio(status); var factory = new CountingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var receiver = new Receiver("synthetic", "Synthetic", "192.0.2.1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(receiver, new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PairAsync(receiver, new(), (_, _) => throw new InvalidOperationException("PIN must not open")));
        Assert.Equal(0, factory.Count); Assert.Equal(0, audio.Mutes); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }

    [Fact]
    public async Task Cleanup_can_expose_pending_recovery_and_admission_rechecks_before_open()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Ready)
        { OnRestore = (value, _) => value.Status = MuteRecoveryStatus.Pending };
        var factory = new CountingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(new("synthetic", "Synthetic", "192.0.2.1"), new()));
        Assert.Equal(0, factory.Count); Assert.Equal(0, audio.Mutes);
    }

    [Fact]
    public async Task Rejected_replacement_publishes_idle_after_the_old_worker_has_stopped()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Ready); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += value => { if (value.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        await controller.StartAsync(new("first", "First", "192.0.2.1"), new());
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));
        audio.OnRestore = (value, _) => value.Status = MuteRecoveryStatus.Pending;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(new("second", "Second", "192.0.2.2"), new()));
        Assert.Equal(1, factory.Count); Assert.Equal(1, factory.Disposed);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Receiver);
    }

    [Fact]
    public async Task Playback_without_local_muting_does_not_require_a_recovery_decision()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Pending); var factory = new CountingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        await controller.StartAsync(new("synthetic", "Synthetic", "192.0.2.1"), new() { MuteWhileStreaming = false });
        await factory.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await controller.StopAsync();
        Assert.Equal(1, factory.Count); Assert.Equal(0, audio.Mutes); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }

    [Theory]
    [InlineData(MuteRecoveryStatus.NotInitialized)]
    [InlineData(MuteRecoveryStatus.Pending)]
    [InlineData(MuteRecoveryStatus.Busy)]
    [InlineData(MuteRecoveryStatus.Unreadable)]
    [InlineData(MuteRecoveryStatus.Unrecognized)]
    public async Task Application_capture_preserves_unresolved_recovery_without_muting_an_endpoint(MuteRecoveryStatus status)
    {
        var audio = new RecoveryAudio(status); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var settings = new AppSettings { CaptureMode = "application", CaptureApplicationPath = "synthetic-application.exe", MuteWhileStreaming = true };
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += value => { if (value.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        await controller.StartAsync(new("synthetic", "Synthetic", "192.0.2.1"), settings);
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, factory.Count); Assert.Equal(0, audio.Mutes); Assert.Equal(status, audio.Recovery.Status);
        Assert.False(settings.ShouldMuteEndpoint);
        await controller.StopAsync();
        Assert.Equal(0, audio.Mutes); Assert.Equal(status, audio.Recovery.Status); Assert.Equal(1, factory.Disposed);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("endpoint")]
    public async Task Endpoint_capture_including_a_selected_recording_input_still_requires_ready_recovery(string mode)
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Pending); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var settings = new AppSettings { CaptureMode = mode, CaptureEndpoint = "synthetic-recording", MuteWhileStreaming = true };
        Assert.True(settings.ShouldMuteEndpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(new("synthetic", "Synthetic", "192.0.2.1"), settings));
        Assert.Equal(0, factory.Count); Assert.Equal(0, audio.Mutes); Assert.Equal(MuteRecoveryStatus.Pending, audio.Recovery.Status);
    }

    [Fact]
    public async Task Application_capture_pairing_does_not_discard_or_mutate_pending_recovery()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Pending); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        await controller.PairAsync(new("synthetic", "Synthetic", "192.0.2.1"),
            new() { CaptureMode = "application", CaptureApplicationPath = "synthetic-application.exe", MuteWhileStreaming = true },
            (_, _) => throw new InvalidOperationException("Pair remains before the PIN prompt."));
        await factory.PairEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackState.Pairing, controller.Snapshot.State);
        Assert.Equal(0, audio.Mutes); Assert.Equal(MuteRecoveryStatus.Pending, audio.Recovery.Status);
        await controller.StopAsync();
        Assert.Equal(1, factory.Count); Assert.Equal(1, factory.Disposed); Assert.Equal(MuteRecoveryStatus.Pending, audio.Recovery.Status);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("receiver")]
    [InlineData("start")]
    public async Task Rejected_endpoint_replacement_keeps_the_application_worker_receiver_and_settings(string operation)
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Pending); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var original = new Receiver("original", "Original application capture", "192.0.2.1");
        var settings = new AppSettings { CaptureMode = "application", CaptureApplicationPath = "synthetic-application.exe", MuteWhileStreaming = true };
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += value => { if (value.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        await controller.StartAsync(original, settings);
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var proposed = settings.Clone(); proposed.CaptureMode = "loopback"; proposed.MasterVolume = 13;
        var replacement = new Receiver("replacement", "Replacement", "192.0.2.2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation switch
        {
            "settings" => controller.UpdateSettingsAsync(proposed),
            "receiver" => controller.UpdateReceiverAsync(replacement, proposed),
            _ => controller.StartAsync(replacement, proposed)
        });
        Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State); Assert.Equal(original, controller.Snapshot.Receiver);
        Assert.Equal(1, factory.Count); Assert.Equal(0, factory.Disposed); Assert.Equal(0, audio.Mutes);
        // A harmless edit using the original capture settings must remain hot.
        // Assigning rejected settings early would instead restart this worker.
        var hot = settings.Clone(); hot.MasterVolume = 37;
        await controller.UpdateSettingsAsync(hot);
        Assert.Equal(1, factory.Count); Assert.Equal(0, factory.Disposed); Assert.Equal(0, audio.Mutes);
        Assert.Equal("application", factory.Commands.Single(c => c.Name == "start").Parameters.Text("source"));
        Assert.Equal(0.37, factory.Commands.Last(c => c.Name == "set_gain").Parameters.GetProperty("gain").GetDouble(), 8);
        Assert.Equal(MuteRecoveryStatus.Pending, audio.Recovery.Status);
    }

    [Fact]
    public async Task Replacement_cleanup_that_becomes_pending_ends_the_old_application_worker_without_starting_endpoint_capture()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Ready); var factory = new StreamingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var settings = new AppSettings { CaptureMode = "application", CaptureApplicationPath = "synthetic-application.exe", MuteWhileStreaming = true };
        var receiver = new Receiver("original", "Original", "192.0.2.1");
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += value => { if (value.State == PlaybackState.Streaming) streaming.TrySetResult(); };
        await controller.StartAsync(receiver, settings); await streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));
        audio.OnRestore = (value, _) => value.Status = MuteRecoveryStatus.Pending;
        var replacement = settings.Clone(); replacement.CaptureMode = "loopback";
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateSettingsAsync(replacement));
        Assert.Equal(1, factory.Count); Assert.Equal(1, factory.Disposed); Assert.Equal(0, audio.Mutes);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Receiver);
        Assert.Equal("application", factory.Commands.Single(c => c.Name == "start").Parameters.Text("source"));
        Assert.Equal(MuteRecoveryStatus.Pending, audio.Recovery.Status);
    }

    [Fact]
    public async Task Failed_cleanup_blocks_a_new_retry_before_another_process()
    {
        var audio = new RecoveryAudio(MuteRecoveryStatus.Ready)
        { OnRestore = (value, count) => { if (count >= 2) value.Status = MuteRecoveryStatus.Pending; } };
        var factory = new CountingFactory();
        await using var controller = TestSessionController.Create(factory, audio);
        var rejectedRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += value => { if (value.State == PlaybackState.Error && value.Message.Contains("recovery", StringComparison.OrdinalIgnoreCase)) rejectedRetry.TrySetResult(); };
        await controller.StartAsync(new("synthetic", "Synthetic", "192.0.2.1"), new() { ForceReconnect = true });
        await rejectedRetry.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, factory.Count); Assert.Equal(0, audio.Mutes);
    }

    private sealed class CountingFactory : IEngineFactory
    {
        public int Count;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref Count); Opened.TrySetResult();
            throw new IOException("Synthetic engine open failure");
        }
    }
    private sealed class StreamingFactory : IEngineFactory
    {
        public int Count, Disposed;
        public TaskCompletionSource PairEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Name, JsonElement Parameters)> Commands { get; } = new();
        public IEngineConnection Open() { ++Count; return new Connection(this); }
        private sealed class Connection(StreamingFactory factory) : IEngineConnection
        {
            private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
            public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                factory.Commands.Enqueue((command, JsonSerializer.SerializeToElement(parameters)));
                if (command == "hello") _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_ownership_guard", "application_capture" } }));
                if (command == "pair") factory.PairEntered.TrySetResult();
                if (command == "start") _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, id = "synthetic", session_id = session, @event = "streaming" }));
                return Task.CompletedTask;
            }
            public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) => await _events.Reader.ReadAsync(cancellation);
            public ValueTask DisposeAsync() { ++factory.Disposed; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class RecoveryAudio(MuteRecoveryStatus status) : IAudioService, IMuteRecoveryService
    {
        public MuteRecoveryStatus Status { get; set; } = status;
        public MuteRecoverySnapshot Recovery => new(1, Status, []);
        public int Mutes, Restores;
        public Action<RecoveryAudio, int>? OnRestore;
        public event Action? EndpointsChanged { add { } remove { } }
        public event Action? RecoveryChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId) { ++Mutes; return Task.CompletedTask; }
        public Task RestoreAsync() { OnRestore?.Invoke(this, ++Restores); return Task.CompletedTask; }
        public Task InitializeRecoveryAsync() => Task.CompletedTask;
        public Task RefreshRecoveryAsync() => Task.CompletedTask;
        public Task ResolveRecoveryAsync(long revision, Guid owner, long generation, string endpointId, MuteRecoveryChoice choice) => Task.CompletedTask;
        public Task ArchiveUnrecognizedRecoveryAsync(long revision) => Task.CompletedTask;
    }
}
