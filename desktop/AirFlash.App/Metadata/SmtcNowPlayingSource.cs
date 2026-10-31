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
    public async Task ExecuteAsync(RemoteCommand command, CancellationToken cancellation)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).ConfigureAwait(false);
            var session = _manager.GetCurrentSession();
            if (session is null) return;
            if (command.Name == "play") await session.TryPlayAsync().AsTask(cancellation).ConfigureAwait(false);
            else if (command.Name == "pause") await session.TryPauseAsync().AsTask(cancellation).ConfigureAwait(false);
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
            var timeline = session.GetTimelineProperties();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new AirFlash.Core.NowPlaying(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                properties.Genres?.FirstOrDefault() ?? "",
                playing,
                Math.Max(0, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds),
                Math.Max(0, (long)(timeline.Position - timeline.StartTime).TotalMilliseconds));
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
}
