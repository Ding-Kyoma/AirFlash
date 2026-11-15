using System.Globalization;
namespace AirFlash.Core;

/// <summary>
/// One minute of the numbers that decide whether an audible artifact came from
/// the sender, the link or the capture endpoint.
/// </summary>
/// <remarks>
/// The fixed latency modes cannot self-correct, and the previous log carried no
/// periodic sample at all, so a report of "it crackles after half an hour" could
/// not be lined up with anything. This record keeps the counters the desktop
/// already receives, plus the receiver PTP numbers that make a Wi-Fi hiccup.
/// </remarks>
public sealed record HealthSample(long UptimeMs, int TargetMs, double? P99Ms, double? MaxSendLatenessUs,
    long RtxRequests, long CaptureUnderruns, long Rebinds, long SilentProbes, double? FeedbackRttMs,
    double? ClockOffsetUs, double? ClockJitterUs)
{
    public static HealthSample From(TransportMetrics metrics, StreamMetrics? capture, int targetMs)
    {
        var member = metrics.Members.FirstOrDefault(m => m.PtpOffsetNs is not null || m.FeedbackRttMs is not null)
            ?? metrics.Members.FirstOrDefault();
        return new(
            (long)(metrics.SessionUptimeMs ?? 0),
            targetMs,
            metrics.IntervalLatenessP99Ms ?? metrics.LatenessP99Ms,
            metrics.MaxSendLatenessUs,
            metrics.Members.Sum(m => m.RetransmitRequests ?? 0),
            capture?.Underruns ?? 0,
            capture?.Rebinds ?? 0,
            capture?.SilentProbes ?? 0,
            member?.FeedbackRttMs,
            member?.PtpOffsetNs / 1000.0,
            member?.PtpJitterUs);
    }
    /// <summary>Stable `key=value; ...` fields with an em dash for missing values.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"uptime_s={UptimeMs / 1000}; target_ms={TargetMs}; p99_ms={F(P99Ms, "F3")}; max_send_us={F(MaxSendLatenessUs, "F0")}; rtx_req={RtxRequests}; capture_underruns={CaptureUnderruns}; capture_rebinds={Rebinds}; silent_probes={SilentProbes}; feedback_rtt_ms={F(FeedbackRttMs, "F1")}; clock_offset_us={F(ClockOffsetUs, "F1")}; clock_jitter_us={F(ClockJitterUs, "F1")}");
    private static string F(double? value, string format) => value is { } number ? number.ToString(format, CultureInfo.InvariantCulture) : "—";
}

public static class SessionHealth
{
    /// <summary>One line per minute of streaming, in every latency mode.</summary>
    public const long IntervalMs = 60_000;
    /// <summary>A tight-target warning is worth repeating only every five minutes.</summary>
    public const long AdvisoryIntervalMs = 300_000;
    public static bool Due(long uptimeMs, long lastUptimeMs) => uptimeMs >= lastUptimeMs + IntervalMs;
    public static bool AdvisoryDue(long uptimeMs, long lastUptimeMs) => uptimeMs >= lastUptimeMs + AdvisoryIntervalMs;
    /// <summary>
    /// Evidence that the requested target no longer covers the path. Deliberately
    /// advisory: a forced or custom latency stays authoritative, but the log says
    /// when the numbers stopped fitting.
    /// </summary>
    public static bool LooksTight(HealthSample now, HealthSample? before) =>
        now.P99Ms >= 3.0
        || now.RtxRequests - (before?.RtxRequests ?? now.RtxRequests) >= 20
        || now.Rebinds > (before?.Rebinds ?? now.Rebinds) && now.CaptureUnderruns > (before?.CaptureUnderruns ?? now.CaptureUnderruns)
        || now.FeedbackRttMs >= 50.0;
}
