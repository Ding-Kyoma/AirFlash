using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LatenessWatchTests
{
    [Fact]
    public void A_single_spike_does_not_trigger()
    {
        var watch = new LatenessWatch(3.0, 10);
        for (var i = 0; i < 9; i++) Assert.False(watch.Observe(9.0));
        Assert.False(watch.Observe(0.2));
        Assert.False(watch.Observe(9.0));
    }

    [Fact]
    public void Ten_consecutive_late_samples_trigger_exactly_once()
    {
        var watch = new LatenessWatch(3.0, 10);
        var triggers = 0;
        for (var i = 0; i < 10; i++) if (watch.Observe(4.0)) triggers++;
        Assert.Equal(1, triggers);
        // Another full window is needed before it fires again.
        for (var i = 0; i < 10; i++) if (watch.Observe(4.0)) triggers++;
        Assert.Equal(2, triggers);
    }

    [Fact]
    public void A_clean_sample_resets_the_streak()
    {
        var watch = new LatenessWatch(3.0, 4);
        Assert.False(watch.Observe(8.0));
        Assert.False(watch.Observe(8.0));
        Assert.False(watch.Observe(1.0));
        Assert.Equal(0, watch.Streak);
        Assert.False(watch.Observe(8.0));
        Assert.False(watch.Observe(8.0));
        Assert.False(watch.Observe(8.0));
        Assert.True(watch.Observe(8.0));
    }

    [Fact]
    public void A_sample_exactly_at_the_threshold_counts_as_late()
    {
        var watch = new LatenessWatch(3.0, 2);
        Assert.False(watch.Observe(3.0));
        Assert.True(watch.Observe(3.0));
    }

    [Fact]
    public void Missing_metrics_never_trigger()
    {
        var watch = new LatenessWatch();
        for (var i = 0; i < 50; i++) Assert.False(watch.Observe(null));
        Assert.Equal(0, watch.Streak);
        watch.Reset();
        Assert.Equal(0, watch.Streak);
    }
}
