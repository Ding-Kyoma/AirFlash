using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class PairingPasswordTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly SessionTiming Timing = new(Limit, Limit, Limit, TimeSpan.Zero);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Receiver Receiver(bool password) => new("synthetic-password", "Synthetic receiver", "192.0.2.81", 7001) { RequiresPassword = password };
    private static AppSettings Settings() => new() { MuteWhileStreaming = true, ForceReconnect = true, MaxReconnectAttempts = 1 };

    [Theory]
    [InlineData(true, "password_required", "  raw-pässword-12-34 \t", true)]
    [InlineData(false, "password_required", "raw-password-12-34", true)]
    [InlineData(false, "pin_required", "1234", false)]
    public async Task PairingPreservesPasswordFlagPromptKindAndExactRawPayload(bool required, string prompt, string value, bool passwordPrompt)
    {
        var connection = new Connection(prompt); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        Receiver? requested = null;
        await controller.PairAsync(Receiver(required), Settings(), (member, _) => { requested = member; return Task.FromResult<string?>(value); });
        await connection.PinSent.Task.WaitAsync(Limit);
        var pair = connection.Commands.Single(c => c.Name == "pair").Parameters;
        Assert.Equal(required, pair.GetProperty("password_required").GetBoolean());
        Assert.Equal(7001, pair.GetProperty("peer").GetProperty("port").GetInt32());
        Assert.Equal("192.0.2.81", pair.GetProperty("peer").Text("host"));
        Assert.NotNull(requested); Assert.Equal(passwordPrompt, requested.RequiresPassword);
        Assert.Equal(value, connection.Commands.Single(c => c.Name == "pair_pin").Parameters.Text("pin"));
        Assert.Equal(PlaybackState.Pairing, controller.Snapshot.State);
        await controller.StopAsync();
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        Assert.DoesNotContain(connection.Commands, c => c.Name == "start");
        Assert.True(connection.Disposed.Task.IsCompletedSuccessfully);
    }

    public static IEnumerable<object[]> NonemptyWhitespacePasswords()
    {
        foreach (var required in new[] { false, true })
        foreach (var value in new[] { " ", "\t", "\u00a0", " \t ", "\u00a0 \t", "\u2003" })
            yield return [required, value];
    }

    [Theory]
    [MemberData(nameof(NonemptyWhitespacePasswords))]
    public async Task ActualPasswordPromptSubmitsNonemptyWhitespaceUnchangedBeforePaired(bool required, string value)
    {
        var connection = new Connection("password_required"); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        Receiver? requested = null;
        await controller.PairAsync(Receiver(required), Settings(), (member, _) =>
        {
            requested = member; return Task.FromResult<string?>(value);
        });
        // The unchanged controller cancels and disposes instead of submitting.
        // Observe either outcome explicitly rather than waiting out a timeout.
        await Task.WhenAny(connection.PinSent.Task, connection.Disposed.Task).WaitAsync(Limit);
        Assert.True(connection.PinSent.Task.IsCompletedSuccessfully, "A nonempty password must be submitted unchanged, including whitespace-only passwords.");
        Assert.NotNull(requested); Assert.True(requested.RequiresPassword);
        Assert.Equal(required, connection.Commands.Single(command => command.Name == "pair").Parameters.GetProperty("password_required").GetBoolean());
        Assert.Equal(value, Assert.Single(connection.Commands, command => command.Name == "pair_pin").Parameters.Text("pin"));
        // No paired event is scripted. Submission alone cannot start playback.
        Assert.Equal(PlaybackState.Pairing, controller.Snapshot.State);
        Assert.DoesNotContain(connection.Commands, command => command.Name == "start");
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        await controller.StopAsync();
        Assert.True(connection.Disposed.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false, " ")]
    [InlineData(true, " ")]
    [InlineData(false, "\t")]
    [InlineData(true, "\t")]
    [InlineData(false, "\u00a0")]
    [InlineData(true, "\u00a0")]
    public async Task ActualPinPromptStillCancelsWhitespaceRegardlessOfAdvertisedPassword(bool required, string value)
    {
        var connection = new Connection("pin_required"); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        Receiver? requested = null;
        await controller.PairAsync(Receiver(required), Settings(), (member, _) =>
        {
            requested = member; return Task.FromResult<string?>(value);
        });
        await connection.Disposed.Task.WaitAsync(Limit);
        Assert.NotNull(requested); Assert.False(requested.RequiresPassword);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        Assert.DoesNotContain(connection.Commands, command => command.Name is "pair_pin" or "start");
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
    }

    public static IEnumerable<object[]> InvalidPasswords()
    {
        yield return [new string('x', 257)];
        yield return [new string('界', 86)];
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("before\0after")]
    [InlineData("before\rafter")]
    [InlineData("before\nafter")]
    [MemberData(nameof(InvalidPasswords))]
    public async Task InvalidOrCancelledPasswordDoesNotSubmitOrStartPlayback(string? value)
    {
        var connection = new Connection("password_required"); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await controller.PairAsync(Receiver(true), Settings(), (_, _) => Task.FromResult(value));
        await Task.WhenAny(connection.PinSent.Task, connection.Disposed.Task).WaitAsync(Limit);
        Assert.False(connection.PinSent.Task.IsCompleted);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        Assert.DoesNotContain(connection.Commands, c => c.Name is "pair_pin" or "start");
    }

    [Fact]
    public Task MalformedUtf16CallbackCannotSubmitReplacementCharacters()
        => InvalidOrCancelledPasswordDoesNotSubmitOrStartPlayback(new string('\ud800', 1));

    [Fact]
    public async Task MissingPasswordCapabilityRejectsBeforePromptPairOrPlayback()
    {
        var connection = new Connection("password_required", passwordCapability: false);
        var factory = new Factory(connection); var audio = new Audio(); var fault = Signal(); var prompts = 0;
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        controller.Changed += snapshot => { if (snapshot.State == PlaybackState.Error) fault.TrySetResult(); };
        await controller.PairAsync(Receiver(true), Settings(), (_, _) => { ++prompts; return Task.FromResult<string?>("never submitted"); });
        await fault.Task.WaitAsync(Limit); await connection.Disposed.Task.WaitAsync(Limit);
        Assert.Equal(0, prompts); Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        Assert.DoesNotContain(connection.Commands, c => c.Name is "pair" or "pair_pin" or "start");
        Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
    }

    [Theory]
    [InlineData("12-34", "1234")]
    [InlineData("-12--34-", "1234")]
    [InlineData("12-34-56-78", "12345678")]
    [InlineData("00-00", "0000")]
    public async Task DashedCallbackPinIsNormalizedBeforePairPin(string input, string expected)
    {
        var connection = new Connection("pin_required"); var factory = new Factory(connection); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        Receiver? requested = null;
        await controller.PairAsync(Receiver(false), Settings(), (member, _) =>
        {
            requested = member;
            return Task.FromResult<string?>(input);
        });
        await connection.PinSent.Task.WaitAsync(Limit);
        Assert.NotNull(requested); Assert.False(requested.RequiresPassword);
        Assert.False(connection.Commands.Single(command => command.Name == "pair").Parameters.GetProperty("password_required").GetBoolean());
        Assert.Equal(expected, connection.Commands.Single(command => command.Name == "pair_pin").Parameters.Text("pin"));
        await controller.StopAsync();
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        Assert.DoesNotContain(connection.Commands, command => command.Name == "start");
        Assert.True(connection.Disposed.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("1234 ")]
    [InlineData("１２３４")]
    [InlineData("١٢٣٤")]
    [InlineData("12—34")]
    [InlineData("12a34")]
    public async Task InvalidOrCancelledPinProducesNoSubmissionPlaybackOrMute(string? input)
    {
        var connection = new Connection("pin_required"); var factory = new Factory(connection); var audio = new Audio();
        var prompts = 0;
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await controller.PairAsync(Receiver(false), Settings(), (member, _) =>
        {
            ++prompts; Assert.False(member.RequiresPassword);
            return Task.FromResult<string?>(input);
        });
        await connection.Disposed.Task.WaitAsync(Limit);
        Assert.Equal(1, prompts); Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        Assert.DoesNotContain(connection.Commands, command => command.Name is "pair_pin" or "start");
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }

    private sealed class Factory(Connection connection) : IEngineFactory
    {
        private int _opens;
        public int OpenCount => Volatile.Read(ref _opens);
        public IEngineConnection Open()
        {
            if (Interlocked.Increment(ref _opens) != 1) throw new InvalidOperationException("No playback/retry process is scripted.");
            return connection;
        }
    }
    private sealed class Connection(string prompt, bool passwordCapability = true) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<(string Name, JsonElement Parameters)> Commands { get; } = new();
        public TaskCompletionSource PinSent { get; } = Signal();
        public TaskCompletionSource Disposed { get; } = Signal();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue((command, JsonSerializer.SerializeToElement(parameters)));
            if (command == "hello") Emit(session, new { @event = "hello", capabilities = passwordCapability ? new[] { "receiver_password" } : [] });
            else if (command == "pair") Emit(session, new { @event = prompt });
            else if (command == "pair_pin") PinSent.TrySetResult();
            return Task.CompletedTask;
        }
        private void Emit(string session, object payload)
        {
            var item = JsonSerializer.SerializeToNode(payload)!.AsObject();
            item["version"] = 1; item["session_id"] = session;
            _events.Writer.TryWrite(JsonSerializer.SerializeToElement(item));
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
