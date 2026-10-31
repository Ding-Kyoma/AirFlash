using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiagnosticExportTests
{
    [Fact]
    public void The_bundle_carries_versions_os_and_the_session()
    {
        using var document = JsonDocument.Parse("""{"packets":7}""");
        var text = DiagnosticExport.Compose("0.5.4", "0.1.0", document.RootElement);
        using var bundle = JsonDocument.Parse(text);
        Assert.Equal("0.5.4", bundle.RootElement.Text("app_version"));
        Assert.Equal("0.1.0", bundle.RootElement.Text("engine_version"));
        Assert.False(string.IsNullOrWhiteSpace(bundle.RootElement.Text("os")));
        Assert.Equal(7, bundle.RootElement.GetProperty("session").Integer("packets"));
        Assert.False(bundle.RootElement.TryGetProperty("log_tail", out _));
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
