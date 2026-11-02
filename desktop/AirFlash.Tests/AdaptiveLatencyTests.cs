using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class AdaptiveLatencyTests
{
    private static readonly LatencyPolicy.Plan Pair = new(120, 120, [120, 140, 160, 200], "stereo");
    internal sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private static AdaptiveLatency State(ManualClock clock, int baseline = 120)
    {
        var state = new AdaptiveLatency(clock);
        state.BeginAttempt(baseline);
        state.Accept(Runtime(0, baseline));
        return state;
    }
    private static LatencyRuntime Runtime(long sequence, double baseline = 120, double extra = 0, long epoch = 7)
        => new(epoch, sequence, baseline, extra, baseline + extra, 100, true);
    private static TransportMetrics Metrics(double uptime, long active = 0, double? p99 = 0, long epoch = 7, long idle = 0)
        => new(uptime, 0, 0, 0, [], IntervalLatenessP99Ms: p99, SessionEpoch: epoch,
            UnderrunsActive: active, UnderrunsIdle: idle);
    private static AdaptiveAction Request(AdaptiveLatency state)
    {
        for (var i = 0; i < 3; i++) state.Recovery("sender_late_recovered");
        return Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true));
    }

    [Fact]
    public void RequestDoesNotPublishAppliedUntilCorrelatedConfirmation()
    {
        var clock = new ManualClock(); var state = State(clock);
        var request = Request(state);
        Assert.False(request.Rebuild); Assert.Equal(140, request.Target); Assert.Equal(20, request.ExtraMs);
        Assert.Equal(120, state.Current);
        Assert.False(state.Accept(Runtime(request.Sequence, extra: 20, epoch: 6)));
        Assert.False(state.Accept(Runtime(request.Sequence + 1, extra: 20)));
        Assert.NotNull(state.Pending); Assert.Equal(120, state.Current);
        Assert.True(state.Accept(Runtime(request.Sequence, extra: 20)));
        Assert.Null(state.Pending); Assert.Equal(140, state.Current);
        Assert.False(state.Accept(Runtime(0))); Assert.Equal(140, state.Current);
    }
    [Fact]
    public void TimeoutRebuildsAtRequestedTargetAndRetainsItAcrossAttempts()
    {
        var clock = new ManualClock(); var state = State(clock); Request(state);
        clock.Advance(4.999); Assert.Null(state.Evaluate(Pair, true));
        clock.Advance(.001);
        var action = Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true));
        Assert.True(action.Rebuild); Assert.Equal(140, action.Target);
        Assert.Equal(140, state.RecoveryTarget);
        Assert.True(state.ConsumeRebuild(5)); state.BeginAttempt(140);
        Assert.Equal(140, state.RecoveryTarget); Assert.Equal(1, state.Rebuilds);
        state.Reset(); Assert.Null(state.RecoveryTarget); Assert.Equal(0, state.Rebuilds);
    }
    [Fact]
    public void PeriodicRuntimeCanConfirmAndClampedRuntimeRequestsRebuild()
    {
        var clock = new ManualClock(); var state = State(clock); var request = Request(state);
        var item = JsonSerializer.SerializeToElement(new { session_epoch = 7, lead_sequence = request.Sequence,
            baseline_ms = 120, lead_extra_ms = 10, effective_latency_ms = 130, lead_ceiling_ms = 10, live_lead_supported = true });
        var metrics = TransportMetrics.Parse(item);
        Assert.True(state.Accept(Assert.IsType<LatencyRuntime>(metrics.Latency)));
        Assert.Equal(130, state.Current);
        var action = Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true));
        Assert.True(action.Rebuild); Assert.Equal(140, action.Target);
    }
    [Fact]
    public void ErrorMustMatchBothSequenceAndStreamEpoch()
    {
        var clock = new ManualClock(); var state = State(clock); var request = Request(state);
        state.Reject(request.Sequence + 1, 7, "stale");
        state.Reject(request.Sequence, 6, "stale stream");
        Assert.NotNull(state.Pending);
        state.Reject(request.Sequence, 7, "unsupported");
        Assert.True(Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true)).Rebuild);
    }
    [Fact]
    public void SustainedTroubleCanClimbEveryTenSecondsAndRebuildAtCeiling()
    {
        var clock = new ManualClock(); var state = State(clock);
        foreach (var target in new[] { 140, 160, 200 })
        {
            var request = Request(state);
            Assert.Equal(target, request.Target); Assert.False(request.Rebuild);
            Assert.True(state.Accept(Runtime(request.Sequence, extra: target - 120)));
            for (var second = 0; second < 9; second++)
            {
                clock.Advance(1); for (var i = 0; i < 3; i++) state.Recovery("timeline_reanchored");
                Assert.Null(state.Evaluate(Pair, true));
            }
            clock.Advance(1);
        }
        var ceiling = Request(state);
        Assert.True(ceiling.Rebuild); Assert.Equal(200, ceiling.Target);
    }
    [Fact]
    public void TransientPostChangeTroubleThatSettlesDoesNotEscalate()
    {
        var clock = new ManualClock(); var state = State(clock); var request = Request(state);
        state.Accept(Runtime(request.Sequence, extra: 20));
        clock.Advance(1); Assert.Null(RequestWhileObserving(state));
        clock.Advance(9); Assert.Null(state.Evaluate(Pair, true));
    }
    private static AdaptiveAction? RequestWhileObserving(AdaptiveLatency state)
    {
        for (var i = 0; i < 3; i++) state.Recovery("timeline_reanchored");
        return state.Evaluate(Pair, true);
    }
    [Fact]
    public void ActiveUnderrunsTriggerButIdleCountsAndLifetimeHistogramsDoNot()
    {
        var clock = new ManualClock(); var state = State(clock);
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(1); state.Observe(Metrics(i * 1000, idle: i * 100, p99: null));
            Assert.Null(state.Evaluate(Pair, true));
        }
        for (var i = 1; i <= 3; i++)
        {
            clock.Advance(1); state.Observe(Metrics((12 + i) * 1000, active: i));
        }
        Assert.Equal("sustained active-audio underruns", Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true)).Reason);
    }
    [Fact]
    public void OnlyFreshConsecutiveIntervalsCountTowardLateness()
    {
        var clock = new ManualClock(); var state = State(clock);
        for (var i = 0; i < 20; i++) state.Observe(Metrics(1000, p99: 8));
        Assert.Null(state.Evaluate(Pair, true));
        for (var i = 2; i <= 10; i++) { clock.Advance(1); state.Observe(Metrics(i * 1000, p99: 8)); }
        Assert.Equal("sustained p99 lateness", Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, true)).Reason);
    }
    [Fact]
    public void BaselineComesFromNegotiationAndLargerActualMinimumIsNeverReduced()
    {
        var clock = new ManualClock(); var state = State(clock, 130);
        var action = Request(state);
        Assert.Equal(10, action.ExtraMs); Assert.Equal(140, action.Target);
        var high = State(clock, 300);
        var recovery = Request(high);
        Assert.True(recovery.Rebuild); Assert.Equal(300, recovery.Target);
    }
    [Fact]
    public void UnsupportedOrTooLargeLiveStepRebuildsInsteadOfSendingUnconfirmedLead()
    {
        var clock = new ManualClock(); var state = State(clock);
        for (var i = 0; i < 3; i++) state.Recovery("timeline_reanchored");
        Assert.True(Assert.IsType<AdaptiveAction>(state.Evaluate(Pair, false)).Rebuild);
        state = State(clock, 200);
        for (var i = 0; i < 3; i++) state.Recovery("timeline_reanchored");
        var ntp = new LatencyPolicy.Plan(200, 120, [120, 160, 200, 300, 500], "ntp");
        var first = Assert.IsType<AdaptiveAction>(state.Evaluate(ntp, true));
        state.Accept(Runtime(first.Sequence, 200, 100)); clock.Advance(10);
        for (var i = 0; i < 3; i++) state.Recovery("timeline_reanchored");
        var action = Assert.IsType<AdaptiveAction>(state.Evaluate(ntp, true));
        Assert.True(action.Rebuild); Assert.Equal(500, action.Target);
    }
    [Fact]
    public void RebuildBudgetRequiresSixtySecondsOfFreshHealthyEvidence()
    {
        var clock = new ManualClock(); var state = State(clock);
        for (var i = 0; i < 5; i++) Assert.True(state.ConsumeRebuild(5));
        Assert.False(state.ConsumeRebuild(5));
        state.Observe(Metrics(1000)); clock.Advance(61);
        state.Observe(Metrics(1000)); Assert.Equal(5, state.Rebuilds);
        for (var i = 2; i <= 63; i++) { clock.Advance(1); state.Observe(Metrics(i * 1000)); }
        Assert.Equal(0, state.Rebuilds);
    }
    [Fact]
    public void StandbyResetsStartingTargetAndRejectsOldStreamReceipts()
    {
        var clock = new ManualClock(); var state = State(clock); var request = Request(state);
        state.Accept(Runtime(request.Sequence, extra: 20)); state.Suspend(); state.Resume(120);
        Assert.Null(state.RecoveryTarget); Assert.Equal(120, state.Current);
        Assert.False(state.Accept(Runtime(request.Sequence, extra: 20)));
        state.Accept(Runtime(0, epoch: 8)); Assert.Equal(8, state.Runtime?.SessionEpoch);
    }
}
