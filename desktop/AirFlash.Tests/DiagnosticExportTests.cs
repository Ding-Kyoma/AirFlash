using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiagnosticExportTests
{
    /// <summary>A connection that answers hello, then the diag_summary request.</summary>
    private sealed class Connection(string snapshot) : IEngineConnection
    {
        private readonly Queue<string?> _replies = new([
            """{"event":"hello","version":1,"capabilities":["diagnostics"]}""",
            snapshot,
        ]);
        public List<string> Commands { get; } = [];
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            Commands.Add(command); return Task.CompletedTask;
        }
        public ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            if (_replies.Count == 0) return ValueTask.FromResult<JsonElement?>(null);
            using var document = JsonDocument.Parse(_replies.Dequeue()!);
            return ValueTask.FromResult<JsonElement?>(document.RootElement.Clone());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Factory(IEngineConnection connection) : IEngineFactory { public IEngineConnection Open() => connection; }

    [Fact]
    public async Task A_snapshot_comes_back_from_a_running_engine()
    {
        var connection = new Connection("""{"event":"diag_summary","engine_version":"0.1.0","session":{"packets":1200}}""");
        var snapshot = await DiagnosticExport.SnapshotAsync(new Factory(connection));
        Assert.NotNull(snapshot);
        Assert.Equal("diag_summary", snapshot!.Value.Text("event"));
        Assert.Equal(1200, snapshot.Value.GetProperty("session").Integer("packets"));
        Assert.Equal(["hello", "diag_summary"], connection.Commands);
    }

    [Fact]
    public async Task A_missing_engine_yields_no_snapshot_instead_of_throwing()
    {
        var connection = new Connection("""{"event":"something_else"}""");
        Assert.Null(await DiagnosticExport.SnapshotAsync(new Factory(connection)));
    }

    [Fact]
    public void The_bundle_carries_versions_os_and_the_session()
    {
        using var document = JsonDocument.Parse("""{"packets":7}""");
        var text = DiagnosticExport.Compose("0.5.4", "0.1.0", document.RootElement, ["line one", "line two"]);
        using var bundle = JsonDocument.Parse(text);
        Assert.Equal("0.5.4", bundle.RootElement.Text("app_version"));
        Assert.Equal("0.1.0", bundle.RootElement.Text("engine_version"));
        Assert.False(string.IsNullOrWhiteSpace(bundle.RootElement.Text("os")));
        Assert.Equal(7, bundle.RootElement.GetProperty("session").Integer("packets"));
        Assert.Equal(2, bundle.RootElement.GetProperty("log_tail").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(bundle.RootElement.Text("generated_at")));
    }

    [Fact]
    public void A_bundle_without_a_session_still_parses()
    {
        var text = DiagnosticExport.Compose("0.5.4", "0.1.0", null);
        using var bundle = JsonDocument.Parse(text);
        Assert.False(bundle.RootElement.TryGetProperty("session", out _));
        Assert.False(bundle.RootElement.TryGetProperty("log_tail", out _));
    }

    [Fact]
    public void Writing_the_bundle_creates_a_timestamped_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airflash-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = DiagnosticExport.Write(directory, "{}");
            Assert.True(File.Exists(path));
            Assert.EndsWith(".json", path);
            Assert.Contains("AirFlash-diagnostics-", Path.GetFileName(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
