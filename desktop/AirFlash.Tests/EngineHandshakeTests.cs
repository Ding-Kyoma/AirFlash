using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class EngineHandshakeTests
{
    private sealed class Connection(string? reply, bool stall = false) : IEngineConnection
    {
        public bool Disposed { get; private set; }
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            Assert.Equal("hello", command); return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            if (stall) await Task.Delay(Timeout.Infinite, cancellation);
            if (reply is null) return null;
            using var document = JsonDocument.Parse(reply); return document.RootElement.Clone();
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Factory(Connection connection) : IEngineFactory { public IEngineConnection Open() => connection; }
    [Theory]
    [InlineData(null)]
    [InlineData("{\"event\":\"hello\",\"version\":2}")]
    [InlineData("{\"event\":\"error\",\"version\":1}")]
    public async Task EofAndProtocolMismatchDisposeTheProbe(string? reply)
    {
        var connection = new Connection(reply);
        await Assert.ThrowsAsync<IOException>(() => EngineHandshake.HelloAsync(new Factory(connection)));
        Assert.True(connection.Disposed);
    }
    [Fact]
    public async Task StalledHelloTimesOutAndDisposes()
    {
        var connection = new Connection(null, true);
        await Assert.ThrowsAsync<TimeoutException>(() => EngineHandshake.HelloAsync(new Factory(connection), limit: TimeSpan.FromMilliseconds(50)));
        Assert.True(connection.Disposed);
    }
    [Fact]
    public async Task ShutdownCancellationIsNotMisreportedAsFailure()
    {
        var connection = new Connection(null, true);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EngineHandshake.HelloAsync(new Factory(connection), stop.Token));
        Assert.True(connection.Disposed);
    }
    [Fact]
    public async Task CorrectHelloIsAccepted()
    {
        var connection = new Connection("{\"event\":\"hello\",\"version\":1,\"engine_version\":\"0.1.0\"}");
        var hello = await EngineHandshake.HelloAsync(new Factory(connection));
        Assert.Equal("0.1.0", hello.Text("engine_version")); Assert.True(connection.Disposed);
    }
}
