using System.Text.Json;
namespace AirFlash.Core;

/// <summary>Engine scheduling values, never an acoustic latency measurement.</summary>
public sealed record LatencyRuntime(long SessionEpoch, long Sequence, double BaselineMs,
    double ExtraMs, double EffectiveMs, int CeilingMs, bool LiveSupported)
{
    public static LatencyRuntime? Parse(JsonElement item)
    {
        if (item.Integer("session_epoch") is not > 0 || item.Integer("lead_sequence") is not >= 0
            || item.Number("baseline_ms") is not { } baseline || !double.IsFinite(baseline) || baseline < 0
            || item.Number("lead_extra_ms") is not { } extra || !double.IsFinite(extra) || extra is < 0 or > 100
            || item.Number("effective_latency_ms") is not { } effective || !double.IsFinite(effective)
            || Math.Abs(effective - baseline - extra) > .01
            || item.Integer("lead_ceiling_ms") is not (>= 0 and <= 100)) return null;
        return new(item.Integer("session_epoch")!.Value, item.Integer("lead_sequence")!.Value,
            baseline, extra, effective, (int)item.Integer("lead_ceiling_ms")!.Value,
            item.Boolean("live_lead_supported") == true);
    }
}

internal sealed record AdaptiveAction(bool Rebuild, int Target, string Reason, long Sequence = 0,
    long SessionEpoch = 0, int ExtraMs = 0);
internal sealed record PendingLead(AdaptiveAction Action, long SentAt);

/// <summary>One playback run's confirmation, health windows and bounded recovery budget.</summary>
/// <remarks>Only the session reader calls this state machine. It owns no I/O or timers.</remarks>
internal sealed class AdaptiveLatency(TimeProvider clock)
{
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(5);
    private readonly Queue<long> _recoveries = new();
    private readonly LatenessWatch _lateness = new();
    private long _sequence, _minimumEpoch;
    private long? _observeFrom, _healthySince, _lastRecovery, _lastTrouble;
    private double? _lastUptime, _negotiatedBase;
    private long? _lastUnderruns;
    private int _underrunStreak;
    private string? _trouble;
    private AdaptiveAction? _restart;
    private bool _skipMetrics, _standby;
    public LatencyRuntime? Runtime { get; private set; }
    public PendingLead? Pending { get; private set; }
    public int RequestedBase { get; private set; }
    public int? RecoveryTarget { get; private set; }
    public int Rebuilds { get; private set; }
    public int Current => (int)Math.Round(Runtime?.EffectiveMs ?? _negotiatedBase ?? RequestedBase);
    public TimeSpan? ConfirmationRemaining => Pending is { } pending
        ? ConfirmationTimeout - clock.GetElapsedTime(pending.SentAt) : null;

    public void Reset()
    {
        RecoveryTarget = null; Rebuilds = 0; _sequence = 0; _standby = false;
        BeginAttempt(0);
    }
    public void BeginAttempt(int requested)
    {
        RequestedBase = requested; Runtime = null; Pending = null; _restart = null;
        _minimumEpoch = 0; _negotiatedBase = null; _observeFrom = null;
        _standby = false;
        ResetHealth();
    }
    public void Negotiated(double baseline)
    {
        if (double.IsFinite(baseline) && baseline >= 0)
            _negotiatedBase = Math.Max(_negotiatedBase ?? 0, baseline);
    }
    public void Suspend()
    {
        _standby = true; Pending = null; _restart = null; ResetHealth();
    }
    public void Resume(int initial)
    {
        if (!_standby && Runtime is null) return;
        _minimumEpoch = (Runtime?.SessionEpoch ?? 0) + 1;
        Runtime = null; Pending = null; _restart = null; _negotiatedBase = null;
        if (_standby) { RequestedBase = initial; RecoveryTarget = null; Rebuilds = 0; }
        _observeFrom = null;
        _standby = false; ResetHealth();
    }
    private void ResetHealth()
    {
        _recoveries.Clear(); _lateness.Reset(); _underrunStreak = 0; _trouble = null;
        _lastUptime = null; _lastUnderruns = null; _healthySince = null; _lastRecovery = null;
        _skipMetrics = false;
        _lastTrouble = null;
    }
    public bool Accept(LatencyRuntime runtime)
    {
        if (_standby || runtime.SessionEpoch < _minimumEpoch
            || Runtime is { } previous && previous.SessionEpoch != runtime.SessionEpoch) return false;
        var expected = Pending?.Action.Sequence ?? Runtime?.Sequence ?? 0;
        if (runtime.Sequence != expected && runtime.Sequence != (Runtime?.Sequence ?? 0)) return false;
        if (Runtime is null && RecoveryTarget is not null) _observeFrom = clock.GetTimestamp();
        Runtime = runtime;
        if (Pending is not { } pending || runtime.Sequence != pending.Action.Sequence) return false;
        Pending = null;
        RecoveryTarget = Math.Max(pending.Action.Target, Current);
        ResetHealth(); _skipMetrics = true; _observeFrom = clock.GetTimestamp();
        if (runtime.EffectiveMs < pending.Action.Target - 1)
            _restart = new(true, pending.Action.Target, "lead was clamped below the requested target");
        return true;
    }
    public void Reject(long? sequence, long? epoch, string message)
    {
        if (Pending is not { } pending || sequence != pending.Action.Sequence || epoch != pending.Action.SessionEpoch) return;
        Pending = null;
        _restart = new(true, pending.Action.Target, "lead rejected: " + message);
    }
    public void Recovery(string code)
    {
        if (_standby || code is not ("timeline_reanchored" or "sender_late_recovered")) return;
        var now = clock.GetTimestamp(); _lastRecovery = now; _healthySince = null;
        while (_recoveries.TryPeek(out var at) && clock.GetElapsedTime(at, now) > TimeSpan.FromSeconds(60)) _recoveries.Dequeue();
        _recoveries.Enqueue(now);
        if (_recoveries.Count >= 3) { _trouble = "repeated recoveries"; _lastTrouble = now; }
    }
    public bool Observe(TransportMetrics metrics)
    {
        if (_standby || metrics.SessionEpoch is { } epoch && Runtime?.SessionEpoch is { } active && epoch != active
            || metrics.SessionUptimeMs is not { } uptime || !double.IsFinite(uptime) || uptime < 0
            || _lastUptime is { } last && uptime <= last) return false;
        var gap = _lastUptime is { } prior && uptime - prior > 2500;
        _lastUptime = uptime;
        var delta = metrics.UnderrunsActive is { } count && _lastUnderruns is { } old && count >= old ? count - old : (long?)null;
        _lastUnderruns = metrics.UnderrunsActive;
        if (gap) { _lateness.Reset(); _underrunStreak = 0; _healthySince = null; }
        if (_skipMetrics) { _skipMetrics = false; return true; }
        if (_lateness.Observe(metrics.IntervalLatenessP99Ms)) { _trouble = "sustained p99 lateness"; _lastTrouble = clock.GetTimestamp(); }
        _underrunStreak = delta > 0 ? _underrunStreak + 1 : 0;
        if (_underrunStreak >= 3) { _trouble = "sustained active-audio underruns"; _lastTrouble = clock.GetTimestamp(); }
        var now = clock.GetTimestamp();
        var healthy = delta == 0 && metrics.IntervalLatenessP99Ms is >= 0 and < 3
            && (_lastRecovery is null || clock.GetElapsedTime(_lastRecovery.Value, now) >= TimeSpan.FromSeconds(2));
        if (!healthy) _healthySince = null;
        else
        {
            _healthySince ??= now;
            if (clock.GetElapsedTime(_healthySince.Value, now) >= TimeSpan.FromSeconds(60)) Rebuilds = 0;
        }
        return true;
    }
    public AdaptiveAction? Evaluate(LatencyPolicy.Plan plan, bool confirmedLeadSupported)
    {
        if (_standby) return null;
        if (Pending is { } pending)
        {
            if (clock.GetElapsedTime(pending.SentAt) < ConfirmationTimeout) return null;
            Pending = null;
            _restart = new(true, pending.Action.Target, "lead confirmation timed out");
        }
        if (_restart is { } restart) { _restart = null; RecoveryTarget = restart.Target; return restart; }
        if (_lastTrouble is { } troubleAt && clock.GetElapsedTime(troubleAt) > TimeSpan.FromSeconds(3)) _trouble = null;
        if (_trouble is not { } reason || _observeFrom is { } at && clock.GetElapsedTime(at) < TimeSpan.FromSeconds(10)) return null;
        var target = Math.Max(Current, LatencyPolicy.Next(Current, plan, true));
        _trouble = null;
        var extra = Runtime is { } runtime ? (int)Math.Round(target - runtime.BaselineMs) : 0;
        if (target <= Current || !confirmedLeadSupported || Runtime is not { LiveSupported: true }
            || extra <= 0 || extra > Runtime.CeilingMs)
        {
            RecoveryTarget = target;
            return new(true, target, reason + (target <= Current ? " at the latency ceiling" : " outside live tuning support"));
        }
        var action = new AdaptiveAction(false, target, reason, ++_sequence, Runtime.SessionEpoch, extra);
        Pending = new(action, clock.GetTimestamp());
        _healthySince = null;
        return action;
    }
    public bool ConsumeRebuild(int limit)
    {
        if (Rebuilds >= limit) return false;
        ++Rebuilds; return true;
    }
}
