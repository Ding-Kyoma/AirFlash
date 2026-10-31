using System;
using System.Threading;
using System.Threading.Tasks;
namespace AirFlash.Core;

/// <summary>What the receiver should show right now.</summary>
public sealed record NowPlaying(string Title, string Artist, string Album, string Genre, bool Playing, long DurationMs, long PositionMs)
{
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0 && Album.Length == 0;
    /// <summary>Display delay of a poll that lands between two updates of the same
    /// track: the receiver's progress bar is driven by interpolating from the last
    /// reported start time, so a tiny position change is not worth a request.</summary>
    public const long ProgressThresholdMs = 1500;
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
/// position moved far enough that the receiver's own interpolation would be visibly
/// wrong.
/// </remarks>
public sealed class NowPlayingPump(INowPlayingSource source, Func<long>? clock = null, Action<string>? log = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private NowPlaying? _lastPublished;
    private NowPlaying? _pending;
    private long _pendingSince;
    private bool _hasPending;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);
    public NowPlaying? LastPublished => _lastPublished;
    /// <summary>One poll. Returns the value to send, or null when nothing changed.</summary>
    public async Task<NowPlaying?> PollAsync(CancellationToken cancellation)
    {
        var sample = await source.ReadAsync(cancellation).ConfigureAwait(false);
        if (sample is null || sample.IsEmpty)
        {
            // Nothing playing: keep the last display instead of blanking the speaker.
            _hasPending = false;
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
        if (!ShouldPublish(sample)) return null;
        _lastPublished = sample;
        return sample;
    }
    private static bool SameTrackState(NowPlaying left, NowPlaying right)
        => left.Title == right.Title && left.Artist == right.Artist
        && left.Album == right.Album && left.Genre == right.Genre
        && left.Playing == right.Playing && left.DurationMs == right.DurationMs;
    private bool ShouldPublish(NowPlaying sample)
    {
        if (_lastPublished is not { } last) return true;
        if (last.Title != sample.Title || last.Artist != sample.Artist || last.Album != sample.Album || last.Genre != sample.Genre) return true;
        if (last.Playing != sample.Playing) return true;
        if (Math.Abs(last.DurationMs - sample.DurationMs) > 0) return true;
        // Position only counts once the receiver's interpolation would drift visibly.
        return Math.Abs(sample.PositionMs - last.PositionMs) >= NowPlaying.ProgressThresholdMs;
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
