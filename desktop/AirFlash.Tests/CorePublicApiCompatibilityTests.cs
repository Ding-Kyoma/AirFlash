using System.Text.Json;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class CorePublicApiCompatibilityTests
{
    [Fact]
    public void Original_latency_delegate_uses_the_saved_global_mode()
    {
        var receiver = new Receiver("synthetic-api", "Synthetic API", "192.0.2.31");
        var settings = new AppSettings { LatencyMode = "normal" };
        settings.Options(receiver.Id).CustomBufferMs = 333;
        Func<Receiver, bool> original = settings.UsesDeviceLatency;
        Func<Receiver, string?, bool> projected = settings.UsesDeviceLatency;

        Assert.False(original(receiver));
        settings.LatencyMode = "custom";
        Assert.True(original(receiver));
        Assert.False(projected(receiver, "normal"));
        Assert.True(projected(receiver, "custom"));
        settings.Options(receiver.Id).LatencyMode = "normal";
        Assert.True(original(receiver));
    }

    [Fact]
    public void Original_export_delegate_preserves_the_bundle_without_optional_histories()
    {
        Func<string, string, JsonElement?, string> original = DiagnosticExport.Compose;
        using var summary = JsonDocument.Parse("""{"synthetic_counter":7}""");
        using var bundle = JsonDocument.Parse(original("synthetic-app", "synthetic-engine", summary.RootElement));

        Assert.Equal("synthetic-app", bundle.RootElement.Text("app_version"));
        Assert.Equal("synthetic-engine", bundle.RootElement.Text("engine_version"));
        Assert.Equal(7, bundle.RootElement.GetProperty("session").Integer("synthetic_counter"));
        Assert.False(bundle.RootElement.TryGetProperty("counter_delta_history", out _));
        Assert.False(bundle.RootElement.TryGetProperty("discovery", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capability_overloads_preserve_caller_ownership_and_choose_the_expected_host(bool resolved)
    {
        var receiver = new Receiver("synthetic-api", "Synthetic API", "192.0.2.31");
        var expectedHost = resolved ? "192.0.2.32" : receiver.Address;
        await using var connection = new Connection(receiver, expectedHost);
        var hello = JsonSerializer.SerializeToElement(new { version = 1, @event = "hello", capabilities = new[] { "receiver_capabilities" } });
        Func<IEngineConnection, string, JsonElement, Receiver, CancellationToken, Task<ReceiverCapabilitySet>> original
            = ReceiverCapabilityCatalog.QueryConnectionAsync;

        var result = resolved
            ? await ReceiverCapabilityCatalog.QueryConnectionAsync(connection, "synthetic-session", hello, receiver,
                CancellationToken.None, new Dictionary<string, string> { [receiver.Id] = expectedHost })
            : await original(connection, "synthetic-session", hello, receiver, CancellationToken.None);

        var member = Assert.Single(result.Members);
        Assert.Equal(receiver.Id, member.ReceiverId);
        Assert.Equal(expectedHost, member.Host);
        Assert.Equal("get_receiver_capabilities", connection.Command);
        Assert.Equal(expectedHost, connection.Parameters.GetProperty("peers")[0].Text("host"));
        Assert.Equal(1, connection.Reads);
        Assert.False(connection.Disposed);
    }

    [Fact]
    public async Task Original_capability_delegate_retains_legacy_no_query_behavior()
    {
        var receiver = new Receiver("synthetic-api", "Synthetic API", "must-not-resolve.invalid");
        await using var connection = new Connection(receiver, "unused");
        var hello = JsonSerializer.SerializeToElement(new { version = 1, @event = "hello" });
        Func<IEngineConnection, string, JsonElement, Receiver, CancellationToken, Task<ReceiverCapabilitySet>> original
            = ReceiverCapabilityCatalog.QueryConnectionAsync;

        var result = await original(connection, "synthetic-session", hello, receiver, CancellationToken.None);

        Assert.Same(ReceiverCapabilitySet.Unknown, result);
        Assert.Null(connection.Command);
        Assert.Equal(0, connection.Reads);
        Assert.False(connection.Disposed);
    }

    private sealed class Connection(Receiver receiver, string host) : IEngineConnection
    {
        public string? Command { get; private set; }
        public JsonElement Parameters { get; private set; }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Command = command;
            Parameters = JsonSerializer.SerializeToElement(parameters);
            return Task.CompletedTask;
        }

        public ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                version = 1, session_id = "synthetic-session", @event = "receiver_capabilities",
                members = new[] { new { receiver_id = receiver.Id, host, port = receiver.Port, known = false, profiles = Array.Empty<object>() } }
            }));
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
