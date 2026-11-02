using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LatencyPolicyTests
{
    private static Receiver Solo(string model = "AudioAccessory5,1") => new("z-airplay", "Pod", "10.0.0.5") { Model = model };
    private static Receiver Pair() => new("stereo:a", "Pair", "10.0.0.5") { Members = [new("a", "L", "10.0.0.5") { IsLeader = true }, new("b", "R", "10.0.0.6")] };

    [Fact]
    public void Solo_homepod_alac_starts_at_the_validated_floor()
    {
        var plan = LatencyPolicy.For(Solo());
        Assert.Equal(50, plan.Initial);
        Assert.Equal(50, plan.Floor);
        // The floor is also the bottom of the ladder: it may only grow from here.
        Assert.Equal(60, LatencyPolicy.Next(50, plan, true));
    }

    [Fact]
    public void Pcm_can_go_below_its_starting_lead()
    {
        var plan = LatencyPolicy.For(Solo(), "pcm");
        Assert.Equal(60, plan.Initial);
        Assert.Equal(45, plan.Floor);
        Assert.Equal(45, LatencyPolicy.Next(60, plan, false));
        // Never below the validated floor, no matter how clean the stream is.
        Assert.Equal(45, LatencyPolicy.Next(45, plan, false));
    }

    [Fact]
    public void Both_group_types_start_at_fifty_and_keep_the_upper_recovery_steps()
    {
        foreach (var receiver in new[] { Pair(), Pair() with { Id = "software:pair", IsSoftwareGroup = true } })
        {
            var plan = LatencyPolicy.For(receiver);
            Assert.Equal(50, plan.Initial);
            Assert.Equal(50, plan.Floor);
            Assert.Equal(new[] { 50, 60, 80, 120, 140, 160, 200 }, plan.Ladder);
            Assert.Equal(50, LatencyPolicy.Next(50, plan, false));
            Assert.Equal(60, LatencyPolicy.Next(50, plan, true));
            Assert.Equal(140, LatencyPolicy.Next(120, plan, true));
        }
    }

    [Fact]
    public void Ntp_timing_starts_higher_than_ptp()
    {
        var ptp = LatencyPolicy.For(Solo(), "alac", ptp: true);
        var ntp = LatencyPolicy.For(Solo(), "alac", ptp: false);
        Assert.True(ntp.Initial > ptp.Initial);
        Assert.True(ntp.Floor >= 120);
    }

    [Fact]
    public void Trouble_walks_up_and_stops_at_the_ceiling()
    {
        var plan = LatencyPolicy.For(Solo());
        var lead = plan.Initial;
        for (var step = 0; step < 8; step++) lead = LatencyPolicy.Next(lead, plan, true);
        Assert.Equal(plan.Ceiling, lead);
        Assert.Equal(lead, LatencyPolicy.Next(lead, plan, true));
    }

    [Fact]
    public void Recovery_walks_back_down_to_the_floor_one_step_at_a_time()
    {
        var plan = LatencyPolicy.For(Solo());
        var lead = LatencyPolicy.Next(80, plan, false);
        Assert.Equal(60, lead);
        Assert.Equal(50, LatencyPolicy.Next(lead, plan, false));
        Assert.Equal(50, LatencyPolicy.Next(50, plan, false));
    }

    [Fact]
    public void Adaptive_settings_resolve_a_lead_from_the_topology()
    {
        var settings = new AppSettings { LatencyMode = LatencyPolicy.Mode };
        Assert.Equal(50, settings.Latency(Solo()));
        settings.Options("z-airplay").Codec = "pcm";
        Assert.Equal(60, settings.Latency(Solo()));
        Assert.Equal(50, settings.Latency(Pair()));
        // Other modes keep their historical values.
        settings.LatencyMode = "normal";
        Assert.Equal(200, settings.Latency(Solo()));
        settings.LatencyMode = "realtime";
        Assert.Equal(120, settings.Latency(Solo()));
    }

    [Fact]
    public void Device_override_still_wins_over_the_adaptive_mode()
    {
        var settings = new AppSettings { LatencyMode = LatencyPolicy.Mode };
        var solo = Solo();
        settings.Options(solo.Id).LatencyMode = "buffered";
        Assert.Equal(500, settings.Latency(solo));
        settings.Options(solo.Id).LatencyMode = LatencyPolicy.Mode;
        Assert.Equal(50, settings.Latency(solo));
    }

    [Fact]
    public void Adaptive_is_a_valid_mode_and_survives_save_load()
    {
        var settings = new AppSettings { LatencyMode = LatencyPolicy.Mode };
        Assert.Null(settings.Validate());
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettings.JsonOptions);
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
        Assert.Equal(LatencyPolicy.Mode, saved.LatencyMode);
    }
}
