using System;
using System.Threading;
using System.Threading.Tasks;
namespace AirFlash.Core;

/// <summary>What the receiver should show right now.</summary>
public sealed record NowPlaying(string Title, string Artist, string Album, string Genre, bool Playing, long DurationMs, long PositionMs)
{
    internal double PlaybackRate { get; init; } = 1;
    /// <summary>Nothing displayable: no title, artist or album. Matches the engine's
    /// <c>metadata::NowPlaying::is_empty</c>, which sends nothing to the receiver for
    /// such a value so its display is kept.</summary>
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0 && Album.Length == 0;
    /// <summary>Display delay of a poll that lands between two updates of the same
    /// track: the receiver's progress bar is driven by interpolating from the last
    /// reported start time, so a tiny position change is not worth a request.</summary>
    public const long ProgressThresholdMs = 1500;
    /// <summary>A position reported as of <paramref name="lastUpdated"/>, carried forward
    /// to <paramref name="now"/> while playing and clamped to <c>[0, duration]</c>
    /// (no upper bound when the duration is unknown). An unset (pre-1970) update
    /// time is not extrapolated.</summary>
    public static long CurrentPositionMs(long positionMs, long durationMs, bool playing, DateTimeOffset lastUpdated, DateTimeOffset now)
        => CurrentPositionMs(positionMs, durationMs, playing, lastUpdated, now, 1);

    internal static long CurrentPositionMs(long positionMs, long durationMs, bool playing, DateTimeOffset lastUpdated, DateTimeOffset now, double playbackRate)
        => PositionAfter(positionMs, durationMs, playing, lastUpdated > DateTimeOffset.UnixEpoch ? now - lastUpdated : TimeSpan.Zero, playbackRate);

    internal long PositionAfter(TimeSpan elapsed) => PositionAfter(PositionMs, DurationMs, Playing, elapsed, PlaybackRate);

    private static long PositionAfter(long positionMs, long durationMs, bool playing, TimeSpan elapsed, double playbackRate)
    {
        var position = Math.Max(0, positionMs);
        if (playing && elapsed > TimeSpan.Zero)
        {
            var rate = double.IsFinite(playbackRate) && playbackRate >= 0 ? playbackRate : 1;
            var advance = elapsed.TotalMilliseconds * rate;
            position = advance >= long.MaxValue - position ? long.MaxValue : position + (long)advance;
        }
        return durationMs > 0 ? Math.Min(position, durationMs) : position;
    }
}

/// <summary>Where Now Playing comes from. The Windows implementation reads SMTC;
/// tests replace it.</summary>
public interface INowPlayingSource
{
    Task<NowPlaying?> ReadAsync(CancellationToken cancellation);
}

/// <summary>
/// Polls the media session and decides when a value is worth sending.
/// </summary>
/// <remarks>
/// Two problems this solves, both reported as "the speaker shows the wrong track":
/// SMTC emits bursts of change events while a player settles (title appears before
/// artist, then artwork), and progress moves every poll even when nothing changed.
/// The pump therefore polls on a slow cadence, waits out a short settle window, and
/// only forwards a sample when the tags or the playback state changed, or when the
/// position is far enough from where the receiver's own interpolation has it (the
/// last published position plus the time since, while playing) to be visibly
/// wrong. Steady playback is therefore not re-sent every poll, even though the
/// source reports a moving position.
/// <para>
/// When the source goes away (no media session, or nothing displayable) nothing is
/// forwarded, so an active receiver keeps its display. The pump does forget what it
/// published and raises <see cref="SourceCleared"/> once, so a player that comes back
/// on the same track and position is sent again, and owners can drop their cached
/// copy instead of showing it in a session started later.
/// </para>
/// </remarks>
public sealed class NowPlayingPump(INowPlayingSource source, Func<long>? clock = null, Action<string>? log = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private NowPlaying? _lastPublished;
    private long _publishedAt;
    private NowPlaying? _pending;
    private long _pendingSince;
    private bool _hasPending;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);
    public NowPlaying? LastPublished => _lastPublished;
    /// <summary>Raised by <see cref="PollAsync"/>, on the polling thread, when the
    /// source goes away after a value was published.</summary>
    public event Action? SourceCleared;
    /// <summary>One poll. Returns the value to send, or null when nothing changed.</summary>
    public async Task<NowPlaying?> PollAsync(CancellationToken cancellation)
    {
        var sample = await source.ReadAsync(cancellation).ConfigureAwait(false);
        if (sample is null || sample.IsEmpty)
        {
            // Nothing playing: send nothing, so the speaker keeps its last display.
            _hasPending = false;
            if (_lastPublished is not null)
            {
                _lastPublished = null;
                SourceCleared?.Invoke();
            }
            return null;
        }
        var now = _clock();
        if (!_hasPending || _pending is null || !SameTrackState(_pending, sample))
        {
            // A new value must survive the settle window before it is trusted.
            _pending = sample;
            _pendingSince = now;
            _hasPending = true;
            if (Settle > TimeSpan.Zero) return null;
        }
        else if (now - _pendingSince < (long)Settle.TotalMilliseconds)
        {
            return null;
        }
        if (!ShouldPublish(sample, now)) return null;
        _lastPublished = sample;
        _publishedAt = now;
        return sample;
    }
    private static bool SameTrackState(NowPlaying left, NowPlaying right)
        => left.Title == right.Title && left.Artist == right.Artist
        && left.Album == right.Album && left.Genre == right.Genre
        && left.Playing == right.Playing && left.DurationMs == right.DurationMs && left.PlaybackRate == right.PlaybackRate;
    private bool ShouldPublish(NowPlaying sample, long now)
    {
        if (_lastPublished is not { } last) return true;
        if (last.Title != sample.Title || last.Artist != sample.Artist || last.Album != sample.Album || last.Genre != sample.Genre) return true;
        if (last.Playing != sample.Playing || last.PlaybackRate != sample.PlaybackRate) return true;
        if (Math.Abs(last.DurationMs - sample.DurationMs) > 0) return true;
        // Position only counts once the receiver's interpolation would drift visibly.
        var expected = last.PositionMs + (last.Playing ? Math.Max(0, now - _publishedAt) : 0);
        if (last.DurationMs > 0) expected = Math.Min(expected, last.DurationMs);
        return Math.Abs(sample.PositionMs - expected) >= NowPlaying.ProgressThresholdMs;
    }
    /// <summary>Production loop; stops on cancellation or when the source throws.</summary>
    public async Task RunAsync(Action<NowPlaying> publish, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                if (await PollAsync(cancellation).ConfigureAwait(false) is { } now) publish(now);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error) { log?.Invoke(L.Format("Now Playing poll failed: {0}", error.Message)); }
        }
        while (await WaitAsync(timer, cancellation).ConfigureAwait(false));
    }
    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellation)
    {
        try { return await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
