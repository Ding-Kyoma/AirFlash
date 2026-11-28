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
///
/// The WinRT work runs synchronously on a thread-pool (MTA) thread rather than being
/// awaited. Awaiting the thumbnail stream resumes the continuation on an STA thread,
/// whose apartment cannot call back into the SMTC session: the follow-up
/// <see cref="GlobalSystemMediaTransportControlsSession.TryGetMediaPropertiesAsync"/>
/// then fails with RPC_E_WRONG_THREAD (a COMException with an empty message), and the
/// album cover is discarded. MTA threads share one apartment, so moving between pool
/// threads between polls is safe.
/// </remarks>
public sealed class SmtcNowPlayingSource(Action<string>? log = null, Func<long?>? artworkGeneration = null) : INowPlayingSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private (long Generation, string App, string Title, string Artist, string Album)? _coverKey;
    private long _nextCoverCheck;
    private byte[]? _jpeg;
    public Task ExecuteAsync(RemoteCommand command, CancellationToken cancellation)
        => Task.Run(() => Execute(command, cancellation), cancellation);
    private void Execute(RemoteCommand command, CancellationToken cancellation)
    {
        try
        {
            _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).GetAwaiter().GetResult();
            var session = _manager.GetCurrentSession();
            if (session is null) return;
            if (command.Name == "play") session.TryPlayAsync().AsTask(cancellation).GetAwaiter().GetResult();
            else if (command.Name == "pause") session.TryPauseAsync().AsTask(cancellation).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { log?.Invoke($"Remote media command failed: {Detail(error)}"); }
    }
    public Task<NowPlaying?> ReadAsync(CancellationToken cancellation)
        => Task.Run(() => Read(cancellation), cancellation);
    private NowPlaying? Read(CancellationToken cancellation)
    {
        try
        {
            _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).GetAwaiter().GetResult();
            var session = _manager.GetCurrentSession();
            if (session is null) { _coverKey = null; _jpeg = null; return null; }
            var properties = session.TryGetMediaPropertiesAsync().AsTask(cancellation).GetAwaiter().GetResult();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var now = new AirFlash.Core.NowPlaying(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                properties.Genres?.FirstOrDefault() ?? "",
                playing,
                Math.Max(0, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds),
                Math.Max(0, (long)(timeline.Position - timeline.StartTime).TotalMilliseconds));
            if (artworkGeneration?.Invoke() is not long generation || now.IsEmpty)
            {
                _coverKey = null; _jpeg = null;
                return now;
            }
            var key = (generation, session.SourceAppUserModelId, now.Title, now.Artist, now.Album);
            if (_coverKey != key) { _coverKey = key; _jpeg = null; _nextCoverCheck = 0; }
            if (Environment.TickCount64 >= _nextCoverCheck)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var image = properties.Thumbnail is { } thumb
                        ? PlayerArtworkReader.Read(thumb, timeout.Token) : null;
                    // A slow thumbnail can finish after the player has already switched tracks.
                    if (image is not null)
                    {
                        var current = _manager.GetCurrentSession();
                        if (current is null || current.SourceAppUserModelId != session.SourceAppUserModelId) return null;
                        var check = current.TryGetMediaPropertiesAsync().AsTask(timeout.Token).GetAwaiter().GetResult();
                        if ((check.Title ?? "") != now.Title || (check.Artist ?? "") != now.Artist || (check.AlbumTitle ?? "") != now.Album) return null;
                    }
                    if (image is null || !_jpeg.AsSpan().SequenceEqual(image)) _jpeg = image;
                    _nextCoverCheck = Environment.TickCount64 + (_jpeg is null ? 4000 : 30000);
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                {
                    _nextCoverCheck = Environment.TickCount64 + 10000;
                    log?.Invoke($"Player artwork unavailable: {Detail(error)}");
                }
            }
            return now with { ArtworkGeneration = generation, ArtworkJpeg = _jpeg };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            log?.Invoke(L.Format("Now Playing source unavailable: {0}", Detail(error)));
            // Forget the manager so a later poll can recreate it after the media
            // session restarts.
            _manager = null;
            return null;
        }
    }
    // Keep the HRESULT and type in the log: WinRT failures often carry no message.
    private static string Detail(Exception error) => $"{error.GetType().Name} 0x{error.HResult:X8} {error.Message}".TrimEnd();
}
