using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class RecoveryHealthTests
{
    private static TransportMetrics Metrics(long successes, bool ready = true, bool right = true, long failures = 0)
        => TransportMetrics.Parse(JsonSerializer.SerializeToElement(new { members = (right ? new[] { "a", "b" } : ["a"])
            .Select(host => new { host, ptp_required = true, ptp_ready = ready, health = new { feedback_successes = successes,
                feedback_failures = failures, feedback_delayed = false } }) }));

    [Fact]
    public void RequiresSixtySecondsOfFreshFeedbackFromEveryMember()
    {
        var clock = new AdaptiveLatencyTests.ManualClock(); var health = new RecoveryHealth(clock);
        health.BeginAttempt(["a", "b"]);
        for (var i = 0; i < 60; i++) { Assert.False(health.Observe(Metrics(i + 1))); clock.Advance(1); }
        Assert.True(health.Observe(Metrics(61)));
        clock.Advance(1); Assert.False(health.Observe(Metrics(62)));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("clock")]
    [InlineData("member")]
    [InlineData("gap")]
    [InlineData("failures")]
    [InlineData("attempt")]
    public void InterruptedOrStaleEvidenceCannotReplenishRetries(string interruption)
    {
        var clock = new AdaptiveLatencyTests.ManualClock(); var health = new RecoveryHealth(clock);
        health.BeginAttempt(["a", "b"]);
        var prefix = interruption == "stale" ? 53 : 59;
        for (var i = 0; i < prefix; i++) { Assert.False(health.Observe(Metrics(i + 1))); clock.Advance(1); }
        switch (interruption)
        {
            case "clock": Assert.False(health.Observe(Metrics(60, ready: false))); break;
            case "member": Assert.False(health.Observe(Metrics(60, right: false))); break;
            case "gap": clock.Advance(61); Assert.False(health.Observe(Metrics(60))); break;
            case "failures": Assert.False(health.Observe(Metrics(60, failures: 1))); break;
            case "attempt": health.BeginAttempt(["a", "b"]); break;
            default:
                for (var i = 0; i < 6; i++) { clock.Advance(1); Assert.False(health.Observe(Metrics(prefix))); }
                break;
        }
        clock.Advance(1); Assert.False(health.Observe(Metrics(61)));
    }
}
