using System.Text.Json;
using System.Threading.Channels;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ApplicationCatalogTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnumeratesThroughIndependentIpcAndHonorsEngineCapability(bool supported)
    {
        var connection = new CatalogConnection(supported);
        var catalog = new ApplicationCatalog(new CatalogFactory(connection));
        var items = await catalog.RefreshAsync();
        Assert.Equal(supported, catalog.Supported);
        Assert.Equal(supported ? 1 : 0, items.Count);
        if (supported) { Assert.Equal(42u, items[0].ProcessId); Assert.Equal(123ul, items[0].StartedAt); Assert.Equal(@"C:\Music.exe", items[0].Path); }
        Assert.Equal(supported ? new[] { "hello", "list_applications" } : new[] { "hello" }, connection.Commands);
        Assert.True(connection.Disposed);
    }
    [Fact]
    public void ApplicationCaptureGateDoesNotRaiseHostMinimum()
    {
        Assert.False(WindowsCompatibility.SupportsApplicationCapture(new(10, 0, 19045)));
        Assert.True(WindowsCompatibility.SupportsApplicationCapture(new(10, 0, 20348)));
        Assert.True(WindowsCompatibility.SupportsApplicationCapture(new(10, 0, 26100)));
        Assert.Equal(17763, WindowsCompatibility.MinimumBuild);
    }
    private sealed class CatalogFactory(CatalogConnection connection) : IEngineFactory
    {
        public IEngineConnection Open() => connection;
    }
    private sealed class CatalogConnection(bool supported) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public List<string> Commands { get; } = [];
        public bool Disposed { get; private set; }
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            Commands.Add(command);
            _events.Writer.TryWrite(command == "hello"
                ? JsonSerializer.SerializeToElement(new { @event = "hello", version = 1, capabilities = new { application_capture = supported } })
                : JsonSerializer.SerializeToElement(new { @event = "applications", items = new[] { new { path = @"C:\Music.exe", name = "Music", process_id = 42, started_at = 123 } } }));
            return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) => await _events.Reader.ReadAsync(cancellation);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
