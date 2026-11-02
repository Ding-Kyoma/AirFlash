using System.Text.Json;
namespace AirFlash.Core;

public sealed record MemberTransportMetrics(string Host, long? PacketsSent, long? SendErrors, long? SyncErrors,
    long? RetransmitRequests, long? RetransmitsSent, long? RetransmitMissing, long? RetransmitExpired,
    long? RetransmitQueueDrops, long? RetransmitSendErrors, double? FeedbackRttMs, long? FeedbackFailures,
    bool? FeedbackDelayed, long? EventMessages, double? ReceiverLatencyMs, bool? ReceiverLatencyEstimated)
{
    public long? FeedbackSuccesses { get; init; }
    public bool? PtpReady { get; init; }
    public bool? PtpRequired { get; init; }
    public static MemberTransportMetrics Parse(JsonElement member)
    {
        var media = member.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object ? m : JsonSerializer.SerializeToElement(new { });
        var health = member.TryGetProperty("health", out var h) && h.ValueKind == JsonValueKind.Object ? h : JsonSerializer.SerializeToElement(new { });
        return new MemberTransportMetrics(member.Text("host", "—"), media.Integer("packets_sent"), media.Integer("media_send_errors"), media.Integer("sync_send_errors"),
            media.Integer("retransmit_requests"), media.Integer("retransmits_sent"), media.Integer("retransmit_missing"), media.Integer("retransmit_expired"),
            media.Integer("retransmit_queue_drops"), media.Integer("retransmit_send_errors"), health.Number("feedback_rtt_ms"), health.Integer("feedback_failures"),
            health.Boolean("feedback_delayed"), health.Integer("event_messages"), media.Number("receiver_latency_ms"), media.Boolean("receiver_latency_estimated"))
            { FeedbackSuccesses = health.Integer("feedback_successes"), PtpReady = member.Boolean("ptp_ready"), PtpRequired = member.Boolean("ptp_required") };
    }
}
public sealed record TransportMetrics(double? SessionUptimeMs, long? SenderLateRecoveries, long? SkippedPackets, double? MaxSendLatenessUs, IReadOnlyList<MemberTransportMetrics> Members, double? LatenessP50Ms = null, double? LatenessP99Ms = null, double? IntervalLatenessP99Ms = null,
    long? SessionEpoch = null, long? UnderrunsActive = null, long? UnderrunsIdle = null, LatencyRuntime? Latency = null)
{
    public static TransportMetrics Parse(JsonElement item)
    {
        var lateness = item.TryGetProperty("lateness", out var histogram) && histogram.ValueKind == JsonValueKind.Object ? histogram : default;
        return new(item.Number("session_uptime_ms"), item.Integer("sender_late_recoveries"), item.Integer("skipped_packets"), item.Number("max_send_lateness_us"),
            item.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array ? members.EnumerateArray().Select(MemberTransportMetrics.Parse).ToArray() : [],
            lateness.ValueKind == JsonValueKind.Object ? lateness.Number("p50_ms") : null,
            lateness.ValueKind == JsonValueKind.Object ? lateness.Number("p99_ms") : null,
            item.TryGetProperty("lateness_interval", out var interval) && interval.ValueKind == JsonValueKind.Object ? interval.Number("p99_ms") : null,
            item.Integer("session_epoch"), item.Integer("underruns_active"), item.Integer("underruns_idle"), LatencyRuntime.Parse(item));
    }
}
public sealed record EngineNotice(string Code, string Host, string Channel, string Message, bool? Retryable = null, string? Reason = null)
{
    public static EngineNotice Parse(JsonElement item)
    {
        var reason = item.Text("reason");
        return new(item.Text("code", "engine_error"), item.Text("host"), item.Text("channel"), item.Text("message", L.Get("Audio engine error.")), item.Boolean("retryable"), string.IsNullOrEmpty(reason) ? null : reason);
    }
    public string Detail => string.Join(" · ", new[] { Message, Host, Channel, Code }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
public sealed record PlaybackDiagnostics(long ReconnectCount = 0, EngineNotice? LastFault = null, StreamMetrics? CaptureBeforeFault = null,
    TransportMetrics? TransportBeforeFault = null, TransportMetrics? Transport = null, IReadOnlyList<EngineNotice>? Warnings = null,
    IReadOnlyList<JsonElement>? Negotiated = null, JsonElement? ClockStatus = null, JsonElement? SchedulerStatus = null);
