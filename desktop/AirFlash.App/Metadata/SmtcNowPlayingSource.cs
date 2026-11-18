using AirFlash.Core;
using Windows.Media.Control;
namespace AirFlash.App.Metadata;

/// <summary>
/// Reads Now Playing from the Windows system media transport controls (SMTC).
/// </summary>
/// <remarks>
/// SMTC is the only supported way for a desktop process to see what the current
/// media app is playing; there is no Win32 equivalent. Every call is wrapped so an
/// environment that refuses the session request (no media app, policy, or a Windows
/// build without the contract) simply produces no metadata instead of failing the
/// stream.
/// </remarks>
public sealed class SmtcNowPlayingSource(Action<string>? log = null) : INowPlayingSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    public async Task ExecuteAsync(RemoteCommand command, Func<bool> isCurrent, CancellationToken cancellation)
    {
        try
        {
            if (!isCurrent()) return;
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).ConfigureAwait(false);
            if (!isCurrent()) return;
            var session = _manager.GetCurrentSession();
            if (session is null || !isCurrent()) return;
            if (command.Name == "play") await session.TryPlayAsync().AsTask(cancellation).ConfigureAwait(false);
            else if (command.Name == "pause") await session.TryPauseAsync().AsTask(cancellation).ConfigureAwait(false);
            else if (command.Name is "shuffle_next" or "repeat_next")
            {
                // Apps that do not expose the control get nothing, rather than a
                // request SMTC would refuse.
                var playback = session.GetPlaybackInfo();
                if (!isCurrent()) return;
                if (command.Name == "shuffle_next" && playback.Controls.IsShuffleEnabled)
                    await session.TryChangeShuffleActiveAsync(playback.IsShuffleActive != true).AsTask(cancellation).ConfigureAwait(false);
                else if (command.Name == "repeat_next" && playback.Controls.IsRepeatEnabled)
                    await session.TryChangeAutoRepeatModeAsync(playback.AutoRepeatMode switch
                    {
                        Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                        Windows.Media.MediaPlaybackAutoRepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.None,
                        _ => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                    }).AsTask(cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { log?.Invoke($"Remote media command failed: {error.Message}"); }
    }
    public async Task<NowPlaying?> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).ConfigureAwait(false);
            var session = _manager.GetCurrentSession();
            if (session is null) return null;
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellation).ConfigureAwait(false);
            var playback = session.GetPlaybackInfo();
            // Some players retain the last track properties after stopping.
            if (!HasMetadata(playback.PlaybackStatus)) return null;
            var timeline = session.GetTimelineProperties();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var rate = playback.PlaybackRate is double reportedRate && double.IsFinite(reportedRate) && reportedRate >= 0 ? reportedRate : 1d;
            var duration = Math.Max(0, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds);
            return new AirFlash.Core.NowPlaying(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                properties.Genres?.FirstOrDefault() ?? "",
                playing,
                duration,
                // SMTC's position is as of LastUpdatedTime, which many apps only
                // refresh on play, pause or seek.
                AirFlash.Core.NowPlaying.CurrentPositionMs(
                    (long)(timeline.Position - timeline.StartTime).TotalMilliseconds,
                    duration, playing, timeline.LastUpdatedTime, DateTimeOffset.UtcNow, rate))
            { PlaybackRate = rate };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            log?.Invoke(L.Format("Now Playing source unavailable: {0}", error.Message));
            // Forget the manager so a later poll can recreate it after the media
            // session restarts.
            _manager = null;
            return null;
        }
    }

    internal static bool HasMetadata(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        status is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
}
