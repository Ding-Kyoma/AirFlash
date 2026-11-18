using System.Diagnostics;
using System.Text;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class EngineRequestCorrelationTests
{
    [Fact]
    public async Task CorrelatedAndOrdinaryRequestsUseTheSameProtocolEnvelope()
    {
        const string script = """
            while ($line = [Console]::ReadLine()) {
                [Console]::Out.WriteLine($line)
                [Console]::Out.Flush()
                if (($line | ConvertFrom-Json).command -eq 'stop') { break }
            }
            """;
        var start = new ProcessStartInfo("pwsh");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        await using var connection = new ProcessEngineConnection(start);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await connection.SendAsync("synthetic-session", "hello", new { marker = "ordinary" }, timeout.Token);
        var ordinary = (await connection.ReadAsync(timeout.Token))!.Value;
        Assert.True(Guid.TryParseExact(ordinary.Text("id"), "N", out _));
        Assert.Equal(1, ordinary.Integer("version"));
        Assert.Equal("synthetic-session", ordinary.Text("session_id"));
        Assert.Equal("hello", ordinary.Text("command"));
        Assert.Equal("ordinary", ordinary.GetProperty("params").Text("marker"));

        const string requestId = "00000000000000000000000000000001";
        await ((ICorrelatedEngineConnection)connection).SendCorrelatedAsync(requestId, "synthetic-session",
            "diag_summary", null, timeout.Token);
        var correlated = (await connection.ReadAsync(timeout.Token))!.Value;
        Assert.Equal(requestId, correlated.Text("id"));
        Assert.Equal(1, correlated.Integer("version"));
        Assert.Equal("synthetic-session", correlated.Text("session_id"));
        Assert.Equal("diag_summary", correlated.Text("command"));
        Assert.Empty(correlated.GetProperty("params").EnumerateObject());

        await connection.SendAsync("synthetic-session", "stop", null, timeout.Token);
    }
}
