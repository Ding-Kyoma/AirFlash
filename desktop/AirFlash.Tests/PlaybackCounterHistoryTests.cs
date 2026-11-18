using System.Text.Json;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class PlaybackCounterHistoryTests
{
    private static readonly DateTimeOffset Receipt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void CaptureDeltasRequireConsecutiveAvailableNondecreasingCounters()
    {
        var history = new PlaybackCounterHistory();
        history.Begin(1, 7);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 4, DroppedFrames: 10) { FullyPaddedPackets = 0 }));
        Assert.Null(history.ObserveCapture(Receipt, new(DroppedFrames: 9) { PaddedFrames = 0, FullyPaddedPackets = 0 }));
        var delta = Assert.Single(history.ObserveCapture(Receipt, new(Underruns: 8, DroppedFrames: 9)
        {
            PaddedFrames = ulong.MaxValue,
            FullyPaddedPackets = 1
        })!);
        Assert.Equal(Receipt, delta.ReceivedAt);
        Assert.Equal(1, delta.Attempt);
        Assert.Equal(7, delta.SessionEpoch);
        Assert.Equal(2, delta.Counters!.Count);
        Assert.Equal(ulong.MaxValue, delta.Counters["padded_frames"]);
        Assert.Equal(1UL, delta.Counters["fully_padded_packets"]);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: -1, DroppedFrames: 9) { PaddedFrames = ulong.MaxValue, FullyPaddedPackets = 1 }));
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 20, DroppedFrames: 9) { PaddedFrames = ulong.MaxValue, FullyPaddedPackets = 1 }));
    }

    [Fact]
    public void TransportComparesUniqueMembersAndRecordsOnlyPositiveDeltas()
    {
        var history = new PlaybackCounterHistory();
        history.Begin(1, null);
        Assert.Null(history.ObserveTransport(Receipt, Transport(7, 2, 3, Member("synthetic-a", 5), Member("synthetic-b", 10))));
        var delta = Assert.Single(history.ObserveTransport(Receipt, Transport(7, 3, 3, Member("synthetic-b", 10), Member("synthetic-a", 7)))!);
        Assert.Equal(7, delta.SessionEpoch);
        Assert.Equal(1UL, Assert.Single(delta.Counters!).Value);
        Assert.Equal("sender_late_recoveries", Assert.Single(delta.Counters!).Key);
        var member = Assert.Single(delta.Members!);
        Assert.Equal(2, member.Member);
        Assert.Equal(2UL, member.Counters["retransmit_requests"]);
        Assert.Equal(2UL, member.Counters["retransmits_sent"]);
        Assert.DoesNotContain("synthetic-", JsonSerializer.Serialize(delta));
        Assert.Null(history.ObserveTransport(Receipt, Transport(7, 3, 3, Member("synthetic-a", 20), Member("synthetic-a", 30))));
        Assert.Null(history.ObserveTransport(Receipt, Transport(7, 3, 3, Member("synthetic-a", 40))));
    }

    [Fact]
    public void AttemptAndTransportChangesResetBaselinesAndRetainHistory()
    {
        var history = new PlaybackCounterHistory();
        history.Begin(1, 7);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 4)));
        Assert.Single(history.ObserveCapture(Receipt, new(Underruns: 5))!);
        history.End();
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 30)));
        history.Begin(1, 8);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 35)));
        Assert.Null(history.ObserveTransport(Receipt, Transport(8, 9, 0)));
        Assert.Null(history.ObserveTransport(Receipt, Transport(7, 200, 0)));
        Assert.Null(history.ObserveTransport(Receipt, Transport(9, 200, 0)));
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 40)));
        history.Begin(2, 7);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 45)));
        var deltas = history.ObserveCapture(Receipt, new(Underruns: 46))!;
        Assert.Equal([1, 2], deltas.Select(delta => delta.Attempt));
        Assert.Equal([7L, 7L], deltas.Select(delta => delta.SessionEpoch!.Value));
    }

    [Fact]
    public void HistoryKeepsOnlyTheMostRecentChangesAndPriorSnapshotsStayStable()
    {
        var history = new PlaybackCounterHistory();
        history.Begin(1, 1);
        Assert.Null(history.ObserveCapture(Receipt, new(Underruns: 0)));
        var first = history.ObserveCapture(Receipt.AddSeconds(1), new(Underruns: 1))!;
        IReadOnlyList<PlaybackCounterDelta> latest = first;
        for (var tick = 2; tick <= PlaybackCounterHistory.Capacity + 1; tick++)
            latest = history.ObserveCapture(Receipt.AddSeconds(tick), new(Underruns: tick))!;
        Assert.Equal(Receipt.AddSeconds(1), Assert.Single(first).ReceivedAt);
        Assert.Equal(PlaybackCounterHistory.Capacity, latest.Count);
        Assert.Equal(Receipt.AddSeconds(2), latest[0].ReceivedAt);
        Assert.All(latest, delta => Assert.Equal(1UL, delta.Counters!["underrun_packets"]));
    }

    private static TransportMetrics Transport(long epoch, long recoveries, long skipped, params MemberTransportMetrics[] members)
        => new(null, recoveries, skipped, null, members, SessionEpoch: epoch);

    private static MemberTransportMetrics Member(string host, long count) => MemberTransportMetrics.Parse(JsonSerializer.SerializeToElement(new
    {
        host, media = new { retransmit_requests = count, retransmits_sent = count, retransmit_expired = 0 }
    }));
}
