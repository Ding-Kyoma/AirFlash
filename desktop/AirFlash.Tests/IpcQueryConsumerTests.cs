using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class IpcQueryConsumerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private const string Message = "synthetic query rejection";
    private const string Rejection = "{\"version\":1,\"id\":\"\",\"session_id\":\"\",\"event\":\"error\",\"code\":\"command_error\",\"channel\":\"ipc\",\"host\":null,\"retryable\":false,\"message\":\"synthetic query rejection\"}";
    private static Receiver Receiver() => new("synthetic-query", "Synthetic query", "192.0.2.41", 7000);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationCatalogPreservesRejectionAtHelloAndListReply(bool atHello)
    {
        var connection = new Connection((session, command) => command == "hello"
            ? atHello ? Rejection : Hello(session, "application_capture")
            : Rejection);
        var factory = new Factory(connection);
        var error = await Assert.ThrowsAsync<EngineFailure>(() => new ApplicationCatalog(factory).RefreshAsync()).WaitAsync(Limit);
        AssertRejection(error);
        Assert.True(connection.Disposed);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(atHello ? new[] { "hello" } : new[] { "hello", "list_applications" }, connection.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationCatalogStillReturnsValidArrayAndObjectCapabilityReplies(bool objectCapabilities)
    {
        var connection = new Connection((session, command) => command == "hello"
            ? Hello(session, "application_capture", objectCapabilities)
            : JsonSerializer.Serialize(new { @event = "applications", items = new[] {
                new { path = "synthetic-player.exe", name = "Synthetic player", process_id = 42, started_at = 123 } } }));
        var catalog = new ApplicationCatalog(new Factory(connection));
        var item = Assert.Single(await catalog.RefreshAsync().WaitAsync(Limit));
        Assert.True(catalog.Supported);
        Assert.Equal("synthetic-player.exe", item.Path);
        Assert.Equal(42u, item.ProcessId);
        Assert.Equal(123ul, item.StartedAt);
        Assert.Equal(new[] { "hello", "list_applications" }, connection.Commands);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidLegacyApplicationHelloReturnsUnsupportedWithoutListing(bool objectCapabilities)
    {
        var connection = new Connection((session, _) => Hello(session, "standby_release", objectCapabilities));
        var catalog = new ApplicationCatalog(new Factory(connection));
        Assert.Empty(await catalog.RefreshAsync().WaitAsync(Limit));
        Assert.False(catalog.Supported);
        Assert.Equal(new[] { "hello" }, connection.Commands);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationCancellationDisposesOnlyItsOwnedQuery(bool atHello)
    {
        var connection = new Connection((session, command) => command == "hello" && !atHello
            ? Hello(session, "application_capture") : null);
        using var cancellation = new CancellationTokenSource();
        var query = new ApplicationCatalog(new Factory(connection)).RefreshAsync(cancellation.Token);
        try
        {
            await (atHello ? connection.HelloReading : connection.QueryReading).Task.WaitAsync(Limit);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query).WaitAsync(Limit);
            Assert.True(connection.Disposed);
            Assert.Equal(atHello ? new[] { "hello" } : new[] { "hello", "list_applications" }, connection.Commands);
        }
        finally { cancellation.Cancel(); await Settle(query); }
    }

    [Theory]
    [InlineData(true, "[]")]
    [InlineData(true, "{")]
    [InlineData(true, "null")]
    [InlineData(true, "42")]
    [InlineData(false, "[]")]
    [InlineData(false, "{")]
    [InlineData(false, "null")]
    [InlineData(false, "42")]
    public async Task ApplicationMalformedRawRepliesBecomeFinalProtocolFailures(bool atHello, string raw)
    {
        var connection = new Connection((session, command) => command == "hello" && !atHello
            ? Hello(session, "application_capture") : raw);
        var error = await Assert.ThrowsAsync<EngineFailure>(() => new ApplicationCatalog(new Factory(connection)).RefreshAsync()).WaitAsync(Limit);
        AssertProtocolFailure(error);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData("wrong-event")]
    [InlineData("wrong-version")]
    [InlineData("missing-version")]
    [InlineData("array")]
    [InlineData("malformed")]
    public async Task InvalidReceiverHelloCannotBecomeLegacyUnknown(string shape)
    {
        var raw = shape switch
        {
            "wrong-event" => "{\"version\":1,\"event\":\"phase\"}",
            "wrong-version" => "{\"version\":2,\"event\":\"hello\"}",
            "missing-version" => "{\"event\":\"hello\"}",
            "array" => "[]",
            _ => "{"
        };
        var connection = new Connection((_, _) => raw);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => new ReceiverCapabilityCatalog(new Factory(connection)).QueryAsync(Receiver())).WaitAsync(Limit);
        if (shape is "array" or "malformed") AssertProtocolFailure(Assert.IsType<EngineFailure>(error));
        Assert.Equal(new[] { "hello" }, connection.Commands);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiverQuerySlotsAreReleasedAfterBothFailuresAndCancellation(bool cancel)
    {
        var first = new Connection((_, _) => null);
        var second = new Connection((_, _) => null);
        var third = new Connection((session, _) => Hello(session, "standby_release"));
        var factory = new Factory(first, second, third);
        var catalog = new ReceiverCapabilityCatalog(factory);
        using var cancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var firstQuery = catalog.QueryAsync(Receiver(), cancellation.Token);
        var secondQuery = catalog.QueryAsync(Receiver(), cancellation.Token);
        Task<ReceiverCapabilitySet>? thirdQuery = null;
        try
        {
            await Task.WhenAll(first.HelloReading.Task, second.HelloReading.Task).WaitAsync(Limit);
            thirdQuery = catalog.QueryAsync(Receiver(), queuedCancellation.Token);
            Assert.Equal(2, factory.OpenCount);
            Assert.False(thirdQuery.IsCompleted);
            if (cancel)
            {
                // The queued request has its own token so it can prove admission
                // after both occupied slots are released.
                cancellation.Cancel();
            }
            else { first.Raw(Rejection); second.Raw(Rejection); }
            var firstError = await Record.ExceptionAsync(() => firstQuery).WaitAsync(Limit);
            var secondError = await Record.ExceptionAsync(() => secondQuery).WaitAsync(Limit);
            var result = await thirdQuery.WaitAsync(Limit);
            Assert.False(result.Known);
            Assert.Equal(3, factory.OpenCount);
            Assert.All(new[] { first, second, third }, connection => Assert.True(connection.Disposed));
            if (cancel)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(firstError);
                Assert.IsAssignableFrom<OperationCanceledException>(secondError);
            }
            else
            {
                AssertRejection(Assert.IsType<EngineFailure>(firstError));
                AssertRejection(Assert.IsType<EngineFailure>(secondError));
            }
        }
        finally
        {
            cancellation.Cancel();
            queuedCancellation.Cancel();
            await Settle(firstQuery);
            await Settle(secondQuery);
            if (thirdQuery is not null) await Settle(thirdQuery);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task ReceiverQueryConnectionNeverDisposesItsSessionsConnection(string outcome)
    {
        var connection = new Connection((session, _) => outcome switch
        {
            "success" => Capabilities(session),
            "rejected" => Rejection,
            _ => null
        });
        using var cancellation = new CancellationTokenSource();
        using var hello = JsonDocument.Parse(Hello("owned-session", "receiver_capabilities"));
        var query = ReceiverCapabilityCatalog.QueryConnectionAsync(connection, "owned-session", hello.RootElement,
            Receiver(), cancellation.Token);
        try
        {
            if (outcome == "cancelled")
            {
                await connection.QueryReading.Task.WaitAsync(Limit);
                cancellation.Cancel();
            }
            var error = await Record.ExceptionAsync(() => query).WaitAsync(Limit);
            Assert.False(connection.Disposed);
            Assert.Equal(new[] { "get_receiver_capabilities" }, connection.Commands);
            if (outcome == "success") { Assert.Null(error); Assert.True((await query).Known); }
            else if (outcome == "rejected") AssertRejection(Assert.IsType<EngineFailure>(error));
            else Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        finally { cancellation.Cancel(); await Settle(query); await connection.DisposeAsync(); }
    }

    [Fact]
    public async Task CorrelatedReceiverErrorRetainsItsExistingStructuredFailure()
    {
        var connection = new Connection((session, command) => command == "hello"
            ? Hello(session, "receiver_capabilities")
            : JsonSerializer.Serialize(new { version = 1, session_id = session, @event = "error",
                code = "authentication_failed", channel = "authentication", host = "192.0.2.41",
                retryable = false, message = "synthetic capability authentication rejected" }));
        var factory = new Factory(connection);
        var error = await Assert.ThrowsAsync<EngineFailure>(() => new ReceiverCapabilityCatalog(factory).QueryAsync(Receiver())).WaitAsync(Limit);
        Assert.Equal("authentication_failed", error.Notice.Code);
        Assert.Equal("authentication", error.Notice.Channel);
        Assert.Equal("192.0.2.41", error.Notice.Host);
        Assert.False(error.Notice.Retryable);
        Assert.Equal("synthetic capability authentication rejected", error.Notice.Message);
        Assert.True(connection.Disposed);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(new[] { "hello", "get_receiver_capabilities" }, connection.Commands);
    }

    public static IEnumerable<object[]> OrdinaryNearMatches()
    {
        foreach (var (key, value) in new (string, object?)[] {
            ("version", 2), ("channel", "setup"), ("host", "192.0.2.42"), ("retryable", true),
            ("id", "ordinary-request"), ("session_id", "ordinary-session"), ("id", null), ("host", false) })
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(Rejection)!;
            data[key] = value;
            foreach (var application in new[] { false, true }) yield return [application, JsonSerializer.Serialize(data)];
        }
    }

    [Theory]
    [MemberData(nameof(OrdinaryNearMatches))]
    public async Task OrdinaryNearMatchesDoNotAcquireProcessOwnedFaultClassification(bool application, string raw)
    {
        var connection = new Connection((session, command) => command == "hello"
            ? Hello(session, application ? "application_capture" : "receiver_capabilities") : raw);
        var factory = new Factory(connection);
        var error = await Record.ExceptionAsync(() => application
            ? (Task)new ApplicationCatalog(factory).RefreshAsync()
            : new ReceiverCapabilityCatalog(factory).QueryAsync(Receiver())).WaitAsync(Limit);
        Assert.Equal(Message, Assert.IsType<IOException>(error).Message);
        Assert.True(connection.Disposed);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public void CapabilityPredicateDoesNotClassifyOrThrowForProcessRejection()
    {
        using var rejection = JsonDocument.Parse(Rejection);
        Assert.False(EngineHandshake.HasCapability(rejection.RootElement, "receiver_capabilities"));
    }

    private static void AssertRejection(EngineFailure error)
    {
        Assert.Equal("command_error", error.Notice.Code);
        Assert.Equal("ipc", error.Notice.Channel);
        Assert.Equal("", error.Notice.Host);
        Assert.Equal(Message, error.Notice.Message);
        Assert.Equal(false, error.Notice.Retryable);
        Assert.Equal(error.Notice.Detail, error.Message);
    }
    private static void AssertProtocolFailure(EngineFailure error)
    {
        Assert.Equal("protocol_error", error.Notice.Code);
        Assert.Equal("ipc", error.Notice.Channel);
        Assert.Equal(false, error.Notice.Retryable);
    }
    private static string Hello(string session, string capability, bool objectCapabilities = false)
        => JsonSerializer.Serialize(new { version = 1, session_id = session, @event = "hello",
            capabilities = objectCapabilities ? (object)new Dictionary<string, bool> { [capability] = true } : new[] { capability } });
    private static string Capabilities(string session) => JsonSerializer.Serialize(new {
        version = 1, session_id = session, @event = "receiver_capabilities", members = new[] {
            new { receiver_id = "synthetic-query", host = "192.0.2.41", port = 7000, known = true, profiles = new[] {
                new { codec = "auto", transport = "auto", timing = "auto", sample_rate = 44100,
                    selected_codec = "alac", selected_transport = "realtime", selected_timing = "ptp" } } } } });
    private static async Task Settle(Task task)
    {
        try { await task.WaitAsync(Limit); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _connections = new(connections);
        private int _opens;
        public int OpenCount => Volatile.Read(ref _opens);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref _opens);
            return _connections.TryDequeue(out var connection) ? connection : throw new IOException("Unexpected query process.");
        }
    }
    private sealed class Connection(Func<string, string, string?> reply) : IEngineConnection
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        private string _command = "";
        public readonly ConcurrentQueue<string> Commands = new();
        public readonly TaskCompletionSource HelloReading = Signal(), QueryReading = Signal();
        public bool Disposed { get; private set; }
        public void Raw(string raw) => _events.Writer.TryWrite(raw);
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            _command = command;
            Commands.Enqueue(command);
            if (reply(session, command) is { } raw) Raw(raw);
            return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            (_command == "hello" ? HelloReading : QueryReading).TrySetResult();
            using var document = JsonDocument.Parse(await _events.Reader.ReadAsync(cancellation));
            return document.RootElement.Clone();
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
