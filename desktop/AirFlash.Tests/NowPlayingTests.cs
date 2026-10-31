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
        clock = 4000; source.Value = Track(position: 4000);
        Assert.Equal(4000, (await pump.PollAsync(default))!.PositionMs);
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
        source.Value = Track();
        await pump.PollAsync(CancellationToken.None);
        now = 400;
        await pump.PollAsync(CancellationToken.None);
        source.Value = null;
        now = 1_000;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        source.Value = new NowPlaying("", "", "", "", true, 0, 0);
        now = 1_400;
        Assert.Null(await pump.PollAsync(CancellationToken.None));
        Assert.NotNull(pump.LastPublished);
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
