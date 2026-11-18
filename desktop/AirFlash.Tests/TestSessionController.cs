using AirFlash.Core;

namespace AirFlash.Tests;

internal static class TestSessionController
{
    // Ordinary controller fixtures must not append to the user's session log.
    // Tests of telemetry can still supply their own in-memory recorder.
    internal static SessionController Create(IEngineFactory factory, IAudioService audio,
        Action<string>? log = null, SessionTiming? timing = null,
        Action<DesktopSessionRecord>? sessionRecords = null, TimeProvider? clock = null)
        => new(factory, audio, log, timing, sessionRecords ?? (_ => { }), clock);
}
