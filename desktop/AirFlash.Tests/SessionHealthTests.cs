using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SessionHealthTests
{
    private static TransportMetrics Metrics(string json) => TransportMetrics.Parse(JsonDocument.Parse(json).RootElement);

    private const string Healthy = """
        {"session_uptime_ms":120000,"max_send_lateness_us":1301,"lateness_interval":{"p99_ms":0.515},"underruns_active":0,
         "members":[{"host":"192.168.1.2","media":{"packets_sent":100,"media_send_errors":0,"retransmit_requests":3,"receiver_latency_ms":200},
         "health":{"feedback_rtt_ms":5.2},"ptp_offset_ns":123456.0,"ptp_round_trip_us":210.0,"ptp_jitter_us":40.0}]}
        """;

    [Fact]
    public void A_health_line_is_due_once_a_minute_not_on_every_metrics_tick()
    {
        Assert.False(SessionHealth.Due(59_999, 0));
        Assert.True(SessionHealth.Due(60_000, 0));
        Assert.False(SessionHealth.Due(119_999, 60_000));
        Assert.True(SessionHealth.Due(120_000, 60_000));
        Assert.False(SessionHealth.AdvisoryDue(299_999, 0));
        Assert.True(SessionHealth.AdvisoryDue(300_000, 0));
    }

    [Fact]
    public void Describe_carries_every_counter_that_can_explain_a_crackle()
    {
        var sample = HealthSample.From(Metrics(Healthy), new StreamMetrics(13.7, 48.7, 0, 40630, 48000, Rebinds: 2, SilentProbes: 1), 200);
        Assert.Equal(
            "uptime_s=120; target_ms=200; p99_ms=0.515; max_send_us=1301; rtx_req=3; capture_underruns=0; "
            + "capture_rebinds=2; silent_probes=1; feedback_rtt_ms=5.2; clock_offset_us=123.5; clock_jitter_us=40.0",
            sample.Describe());
    }

    [Fact]
    public void Missing_values_keep_the_field_and_do_not_print_a_null()
    {
        var metrics = Metrics("""{"session_uptime_ms":60000,"members":[]}""");
        var line = HealthSample.From(metrics, null, 120).Describe();
        Assert.Contains("clock_offset_us=—", line);
        Assert.Contains("feedback_rtt_ms=—", line);
        Assert.Contains("capture_rebinds=0", line);
        Assert.DoesNotContain("null", line);
    }

    [Fact]
    public void Ptp_and_feedback_numbers_survive_the_wire_format()
    {
        var member = Metrics(Healthy).Members.Single();
        Assert.Equal(123456.0, member.PtpOffsetNs!.Value);
        Assert.Equal(210.0, member.PtpRoundTripUs!.Value);
        Assert.Equal(40.0, member.PtpJitterUs!.Value);
        Assert.Equal(5.2, member.FeedbackRttMs!.Value);
    }

    [Fact]
    public void A_healthy_minute_never_warns_about_a_tight_target()
    {
        var now = HealthSample.From(Metrics(Healthy), new StreamMetrics(13.7, 48.7, 0, 40630, 48000, Rebinds: 0, SilentProbes: 0), 200);
        Assert.False(SessionHealth.LooksTight(now, now));
    }

    [Theory]
    [InlineData("""{"session_uptime_ms":120000,"lateness_interval":{"p99_ms":3.2},"members":[]}""")]
    [InlineData("""{"session_uptime_ms":120000,"members":[{"host":"h","health":{"feedback_rtt_ms":93.0}}]}""")]
    public void Lateness_or_a_receiver_rtt_spike_is_enough_evidence(string json)
    {
        var now = HealthSample.From(Metrics(json), null, 200);
        Assert.True(SessionHealth.LooksTight(now, null));
    }

    [Fact]
    public void Retransmit_evidence_is_measured_between_samples_not_by_lifetime_total()
    {
        HealthSample With(long rtx) => HealthSample.From(TransportMetrics.Parse(JsonSerializer.SerializeToElement(new
        {
            session_uptime_ms = 120000,
            members = new[] { new { host = "h", media = new { retransmit_requests = rtx } } }
        })), null, 120);
        var before = With(100);
        Assert.False(SessionHealth.LooksTight(With(119), before));
        Assert.True(SessionHealth.LooksTight(With(120), before));
    }

    [Fact]
    public void Rebinds_only_count_when_audio_also_dropped()
    {
        HealthSample With(long rebinds, long underruns) => HealthSample.From(Metrics(Healthy), new StreamMetrics(Underruns: underruns, Rebinds: rebinds), 200);
        Assert.False(SessionHealth.LooksTight(With(1, 0), With(0, 0)));
        Assert.True(SessionHealth.LooksTight(With(1, 2), With(0, 0)));
    }
}
