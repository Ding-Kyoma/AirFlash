using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NowPlayingTests
{
    [Fact]
    public async Task Continuous_progress_does_not_restart_the_tag_settle_window()
    {
        var source = new Source();
        long clock = 0;
        var pump = new NowPlayingPump(source, () => clock);
        source.Value = Track(position: 0);
        Assert.Null(await pump.PollAsync(default));
        clock = 2000; source.Value = Track(position: 2000);
        Assert.Equal(2000, (await pump.PollAsync(default))!.PositionMs);
        // Steady playback is where the receiver's interpolation already has it.
        clock = 4000; source.Value = Track(position: 4000);
        Assert.Null(await pump.PollAsync(default));
        // A seek is not.
        clock = 6000; source.Value = Track(position: 30_000);
        Assert.Equal(30_000, (await pump.PollAsync(default))!.PositionMs);
    }

    [Fact]
    public async Task Steady_extrapolated_playback_is_not_republished_every_poll()
    {
        var source = new Source();
        long clock = 0;
        var pump = new NowPlayingPump(source, () => clock);
        var published = 0;
        for (var poll = 0; poll <= 30; poll++)
        {
            clock = poll * 2_000L;
            source.Value = Track(position: 10_000 + clock);
            if (await pump.PollAsync(default) is not null) published++;
        }
        Assert.Equal(1, published);
        // A paused track is expected to stay put.
        clock += 2_000; source.Value = Track(position: 10_000 + clock - 2_000, playing: false);
        await pump.PollAsync(default);
        clock += 400;
        Assert.NotNull(await pump.PollAsync(default));
        clock += 2_000;
        Assert.Null(await pump.PollAsync(default));
    }

    [Theory]
    [InlineData(10_000, 240_000, true, 5_000, 15_000)]   // carried forward while playing
    [InlineData(10_000, 240_000, false, 5_000, 10_000)]  // paused: as reported
    [InlineData(238_000, 240_000, true, 5_000, 240_000)] // clamped to the duration
    [InlineData(10_000, 0, true, 5_000, 15_000)]         // unknown duration: no ceiling
    [InlineData(-50, 240_000, false, 0, 0)]              // never negative
    [InlineData(10_000, 240_000, true, -5_000, 10_000)]  // a future update time adds nothing
    public void Smtc_positions_are_carried_to_now(long position, long duration, bool playing, long ageMs, long expected)
    {
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, NowPlaying.CurrentPositionMs(position, duration, playing, now.AddMilliseconds(-ageMs), now));
    }

    [Fact]
    public void An_unset_smtc_update_time_is_not_extrapolated()
        => Assert.Equal(10_000, NowPlaying.CurrentPositionMs(10_000, 0, true, default, DateTimeOffset.UtcNow));

    [Fact]
    public void Smtc_projection_uses_the_reported_playback_rate()
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        Assert.Equal(17_500, NowPlaying.CurrentPositionMs(10_000, 240_000, true, now.AddSeconds(-5), now, 1.5));
    }
    private sealed class Source : INowPlayingSource
    {
        public NowPlaying? Value;
        public int Reads;
        public Task<NowPlaying?> ReadAsync(CancellationToken cancellation)
        {
            Reads++;
            return Task.FromResult(Value);
        }
    }
    private static NowPlaying Track(string title = "Song", long position = 0, bool playing = true) =>
        new(title, "Artist", "Album", "Pop", playing, 240_000, position);
    private static (Source source, NowPlayingPump pump, Func<long> clock) Setup()
    {
        var source = new Source();
        long now = 1_000;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        return (source, pump, () => now);
    }

    [Fact]
    public async Task A_new_track_is_published_only_after_the_settle_window()
    {
        var (source, pump, _) = Setup();
        source.Value = Track();
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        // Same value, still inside the window: nothing yet.
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        Assert.Null(pump.LastPublished);
        Assert.True(source.Reads >= 2);
    }

    [Fact]
    public async Task The_settled_value_is_published_once()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track();
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        now = 400;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));
        // Immediately afterwards, the same sample is not re-sent.
        now = 500;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_small_position_change_is_suppressed_but_a_large_one_is_not()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track(position: 10_000);
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));

        source.Value = Track(position: 11_000);
        now = 2_400;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        now = 2_800;
        Assert.Null(await pump.PollAsync(CancellationToken.None));

        source.Value = Track(position: 12_000);
        now = 4_800;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));
        now = 5_200;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("New Song")]
    [InlineData("Artist")]
    [InlineData("Album")]
    public async Task Tag_changes_win_over_the_progress_threshold(string field)
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track();
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        await pump.PollAsync(CancellationToken.None);

        var previous = pump.LastPublished!;
        source.Value = field switch
        {
            "New Song" => previous with { Title = "New Song" },
            "Artist" => previous with { Artist = "Other" },
            _ => previous with { Album = "Other" },
        };
        now = 1_000;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        now = 1_400;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Pausing_is_reported_even_without_a_position_change()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track(position: 5_000);
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        await pump.PollAsync(CancellationToken.None);

        source.Value = Track(position: 5_000, playing: false);
        now = 1_000;
        await pump.PollAsync(CancellationToken.None);
        now = 1_400;
        var published = await pump.PollAsync(CancellationToken.None);
        Assert.NotNull(published);
        Assert.False(published!.Playing);
    }

    [Fact]
    public async Task Nothing_playing_keeps_the_previous_display()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        var cleared = 0;
        pump.SourceCleared += () => cleared++;
        source.Value = Track();
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        await pump.PollAsync(CancellationToken.None);
        Assert.Equal(0, cleared);
        // Nothing is sent for a missing or empty source, so the receiver keeps
        // its display; the pump only forgets what it published, once.
        source.Value = null;
        now = 1_000;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        source.Value = new NowPlaying("", "", "", "", true, 0, 0);
        now = 1_400;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        Assert.Null(pump.LastPublished);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task A_player_that_comes_back_on_the_same_track_and_position_is_sent_again()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track(position: 30_000, playing: false);
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));
        source.Value = null;
        now = 1_000;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        // Identical to the last published value: still a new value after the
        // player went away, so it settles and is published.
        source.Value = Track(position: 30_000, playing: false);
        now = 2_000;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        now = 2_400;
        Assert.Equal(30_000, (await pump.PollAsync(CancellationToken.None))?.PositionMs);
    }

    [Fact]
    public async Task A_flapping_source_is_not_forwarded_mid_settle()
    {
        var source = new Source();
        long now = 0;
        var pump = new NowPlayingPump(source, () => now) { Settle = TimeSpan.FromMilliseconds(300) };
        source.Value = Track(title: "A");
        await pump.PollAsync(CancellationToken.None);
        now = 100;
        source.Value = Track(title: "B");
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        now = 200;
        source.Value = Track(title: "A");
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        // Only after the final value has been stable for the settle window.
        now = 600;
        Assert.NotNull(await pump.PollAsync(CancellationToken.None));
        Assert.Equal("A", pump.LastPublished!.Title);
    }
}
