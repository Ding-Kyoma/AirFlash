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
        Assert.False(bundle.RootElement.TryGetProperty("counter_delta_history", out _));
        Assert.False(bundle.RootElement.TryGetProperty("discovery", out _));
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
    public void Saved_counter_history_preserves_values_and_omits_private_or_unknown_fields()
    {
        var receipt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        const string privateText = "synthetic-secret@192.0.2.45-C:/Synthetic/Private";
        var history = new PlaybackCounterDelta[]
        {
            new(receipt, 1, null, "capture_metrics", new Dictionary<string, ulong>
            {
                ["padded_frames"] = ulong.MaxValue,
                ["underrun_packets"] = 0,
                [privateText] = 12,
                ["sender_late_recoveries"] = 3
            }),
            new(receipt.AddSeconds(1), 2, 7, "transport_metrics", new Dictionary<string, ulong>
            {
                ["sender_late_recoveries"] = 2, [privateText] = 99
            }, [new(1, new Dictionary<string, ulong>
            {
                ["retransmit_requests"] = 4, ["retransmits_sent"] = 3,
                ["padded_frames"] = 999, [privateText] = 45
            })]),
            new(receipt, 3, 8, privateText, new Dictionary<string, ulong> { ["padded_frames"] = 1 })
        };

        var text = DiagnosticExport.Compose("0.3.2-rc.1", "0.1.0", null, history);
        using var bundle = JsonDocument.Parse(text);
        var saved = bundle.RootElement.GetProperty("counter_delta_history");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.Equal(receipt, saved[0].GetProperty("received_at").GetDateTimeOffset());
        Assert.Equal(1, saved[0].Integer("attempt"));
        Assert.Equal(JsonValueKind.Null, saved[0].GetProperty("session_epoch").ValueKind);
        Assert.Equal(ulong.MaxValue, saved[0].GetProperty("counters").GetProperty("padded_frames").GetUInt64());
        Assert.Single(saved[0].GetProperty("counters").EnumerateObject());
        Assert.Equal(7, saved[1].Integer("session_epoch"));
        Assert.Equal(2UL, saved[1].GetProperty("counters").GetProperty("sender_late_recoveries").GetUInt64());
        var member = Assert.Single(saved[1].GetProperty("members").EnumerateArray());
        Assert.Equal(1, member.Integer("member"));
        Assert.Equal(2, member.GetProperty("counters").EnumerateObject().Count());
        Assert.DoesNotContain(privateText, text);
        Assert.DoesNotContain("host", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saved_history_keeps_only_the_latest_capacity_reports_and_supported_members()
    {
        var receipt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var history = Enumerable.Range(0, PlaybackCounterHistory.Capacity + 4).Select(index =>
            new PlaybackCounterDelta(receipt.AddSeconds(index), 1, 1, "transport_metrics", null,
                Enumerable.Range(0, PlaybackCounterHistory.MemberCapacity + 2).Select(member => new MemberCounterDelta(member,
                    new Dictionary<string, ulong> { ["media_send_errors"] = 1 })).ToArray())).ToArray();

        using var bundle = JsonDocument.Parse(DiagnosticExport.Compose("test", "test", null, history));
        var saved = bundle.RootElement.GetProperty("counter_delta_history");
        Assert.Equal(PlaybackCounterHistory.Capacity, saved.GetArrayLength());
        Assert.Equal(receipt.AddSeconds(4), saved[0].GetProperty("received_at").GetDateTimeOffset());
        Assert.All(saved.EnumerateArray(), report =>
            Assert.Equal(Enumerable.Range(1, PlaybackCounterHistory.MemberCapacity).Select(ordinal => (long)ordinal),
                report.GetProperty("members").EnumerateArray().Select(member => member.Integer("member")!.Value)));
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
