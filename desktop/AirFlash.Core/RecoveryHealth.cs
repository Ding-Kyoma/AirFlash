namespace AirFlash.Core;

/// <summary>Credits retry recovery only after fresh receiver evidence, never UI uptime.</summary>
internal sealed class RecoveryHealth(TimeProvider clock)
{
    private readonly Dictionary<string, (long Successes, long Failures, long FreshAt)> _members = [];
    private HashSet<string> _expected = [];
    private long? _since, _lastSample;
    private bool _credited;

    public void BeginAttempt(IEnumerable<string> hosts)
    {
        _expected = hosts.ToHashSet(StringComparer.Ordinal);
        _members.Clear(); _lastSample = null; ResetStability();
    }
    public void ResetStability() { _since = null; _credited = false; }
    public bool Observe(TransportMetrics metrics)
    {
        var now = clock.GetTimestamp();
        var healthy = _expected.Count > 0 && metrics.Members.Count == _expected.Count
            && _expected.SetEquals(metrics.Members.Select(m => m.Host))
            && (_lastSample is null || clock.GetElapsedTime(_lastSample.Value, now) <= TimeSpan.FromSeconds(3));
        _lastSample = now;
        foreach (var member in metrics.Members)
        {
            if (member.FeedbackSuccesses is not > 0 || member.FeedbackFailures is not { } failures)
            { healthy = false; continue; }
            var successes = member.FeedbackSuccesses.Value;
            var fresh = now;
            if (_members.TryGetValue(member.Host, out var previous))
            {
                if (successes == previous.Successes) fresh = previous.FreshAt;
                if (successes < previous.Successes || failures != previous.Failures) healthy = false;
            }
            _members[member.Host] = (successes, failures, fresh);
            healthy &= member.FeedbackDelayed == false
                && (member.PtpRequired == false || member.PtpRequired == true && member.PtpReady == true)
                && clock.GetElapsedTime(fresh, now) <= TimeSpan.FromSeconds(4);
        }
        if (!healthy) { ResetStability(); return false; }
        _since ??= now;
        if (_credited || clock.GetElapsedTime(_since.Value, now) < TimeSpan.FromSeconds(60)) return false;
        _credited = true;
        return true;
    }
}
