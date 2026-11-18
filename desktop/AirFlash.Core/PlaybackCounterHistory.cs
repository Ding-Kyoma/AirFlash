namespace AirFlash.Core;

/// <summary>Recent counter changes observed by one playback worker. Each entry describes one metrics report.</summary>
internal sealed class PlaybackCounterHistory
{
    internal const int Capacity = 32;
    internal const int MemberCapacity = 128;
    private readonly Queue<PlaybackCounterDelta> _history = new(Capacity);
    private int _attempt;
    private long? _sessionEpoch;
    private StreamMetrics? _capture;
    private TransportMetrics? _transport;

    internal void Begin(int attempt, long? sessionEpoch)
    {
        if (_attempt == attempt && _sessionEpoch == sessionEpoch) return;
        _attempt = attempt;
        _sessionEpoch = sessionEpoch;
        _capture = null;
        _transport = null;
    }

    internal void End() => Begin(0, null);

    internal IReadOnlyList<PlaybackCounterDelta>? ObserveCapture(DateTimeOffset receivedAt, StreamMetrics current)
    {
        if (_attempt == 0) return null;
        var previous = _capture;
        _capture = current;
        if (previous is null) return null;

        Dictionary<string, ulong>? counters = null;
        Add(ref counters, "underrun_packets", Unsigned(previous.Underruns), Unsigned(current.Underruns));
        Add(ref counters, "padded_frames", previous.PaddedFrames, current.PaddedFrames);
        Add(ref counters, "fully_padded_packets", previous.FullyPaddedPackets, current.FullyPaddedPackets);
        Add(ref counters, "dropped_frames", Unsigned(previous.DroppedFrames), Unsigned(current.DroppedFrames));
        return counters is null ? null : Append(new(receivedAt, _attempt, _sessionEpoch, "capture_metrics", counters));
    }

    internal IReadOnlyList<PlaybackCounterDelta>? ObserveTransport(DateTimeOffset receivedAt, TransportMetrics current)
    {
        if (_attempt == 0) return null;
        if (current.SessionEpoch is { } reportedEpoch)
        {
            if (reportedEpoch < _sessionEpoch) return null;
            if (_sessionEpoch is null) _sessionEpoch = reportedEpoch;
            else if (reportedEpoch != _sessionEpoch) Begin(_attempt, reportedEpoch);
        }
        var previous = _transport;
        _transport = current;
        if (previous is null) return null;

        Dictionary<string, ulong>? counters = null;
        Add(ref counters, "sender_late_recoveries", Unsigned(previous.SenderLateRecoveries), Unsigned(current.SenderLateRecoveries));
        Add(ref counters, "skipped_packets", Unsigned(previous.SkippedPackets), Unsigned(current.SkippedPackets));
        List<MemberCounterDelta>? members = null;
        for (var index = 0; index < Math.Min(current.Members.Count, MemberCapacity); index++)
        {
            var member = current.Members[index];
            if (member.Host is "" or "—" || current.Members.Count(item => item.Host == member.Host) != 1) continue;
            var matches = previous.Members.Where(item => item.Host == member.Host).Take(2).ToArray();
            if (matches.Length != 1) continue;
            var before = matches[0];
            Dictionary<string, ulong>? changes = null;
            Add(ref changes, "retransmit_requests", Unsigned(before.RetransmitRequests), Unsigned(member.RetransmitRequests));
            Add(ref changes, "retransmits_sent", Unsigned(before.RetransmitsSent), Unsigned(member.RetransmitsSent));
            Add(ref changes, "retransmit_expired", Unsigned(before.RetransmitExpired), Unsigned(member.RetransmitExpired));
            Add(ref changes, "retransmit_missing", Unsigned(before.RetransmitMissing), Unsigned(member.RetransmitMissing));
            Add(ref changes, "retransmit_queue_drops", Unsigned(before.RetransmitQueueDrops), Unsigned(member.RetransmitQueueDrops));
            Add(ref changes, "media_send_errors", Unsigned(before.SendErrors), Unsigned(member.SendErrors));
            Add(ref changes, "sync_send_errors", Unsigned(before.SyncErrors), Unsigned(member.SyncErrors));
            if (changes is not null) (members ??= []).Add(new(index + 1, changes));
        }
        return counters is null && members is null ? null
            : Append(new(receivedAt, _attempt, _sessionEpoch, "transport_metrics", counters, members));
    }

    private IReadOnlyList<PlaybackCounterDelta> Append(PlaybackCounterDelta delta)
    {
        if (_history.Count == Capacity) _history.Dequeue();
        _history.Enqueue(delta);
        return _history.ToArray();
    }

    private static ulong? Unsigned(long? count) => count is >= 0 ? (ulong)count.Value : null;

    private static void Add(ref Dictionary<string, ulong>? counters, string key, ulong? previous, ulong? current)
    {
        if (previous is { } before && current is { } after && after > before)
            (counters ??= new()).Add(key, after - before);
    }
}
