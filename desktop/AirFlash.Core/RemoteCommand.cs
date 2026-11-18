namespace AirFlash.Core;

internal readonly record struct RemoteCommandAuthority(long Owner, long Intent, string SessionId, long Transport);

/// <summary>Where AirFlash sends a <see cref="RemoteCommand"/>.</summary>
public enum RemoteCommandTarget
{
    /// <summary>Logged only.</summary>
    None,
    /// <summary>The current SMTC media session (play, pause, shuffle, repeat).</summary>
    MediaSession,
    /// <summary>A synthesised media key (<see cref="RemoteCommand.VirtualKey"/>).</summary>
    MediaKey,
    /// <summary>AirFlash's own stream mute, as the Mute button toggles it.</summary>
    Mute,
    /// <summary>AirFlash's master volume, by <see cref="RemoteCommand.VolumeDelta"/>.</summary>
    MasterVolume,
}

/// <summary>
/// A command the receiver sent back over the event channel: the speaker's own
/// buttons, or a remote that talks to it.
/// </summary>
/// <remarks>
/// The engine already answers the acknowledgement, so nothing here has to reply;
/// <see cref="Target"/> says where AirFlash acts on it:
/// <list type="bullet">
/// <item><c>play</c>, <c>pause</c>, <c>shuffle_next</c> and <c>repeat_next</c> go to
/// the current SMTC session (no media key exists for them).</item>
/// <item><c>toggle</c>, <c>stop</c>, <c>next</c> and <c>previous</c> are synthesised
/// as media keys, as the reference sender does instead of reaching for the
/// MediaRemote COM API.</item>
/// <item><c>mute</c>, <c>volume_up</c> and <c>volume_down</c> are requests to the
/// sender, as in DACP: they toggle AirFlash's stream mute or step its master
/// volume by <see cref="VolumeStep"/>. The receiver's own volume is not touched.</item>
/// <item><c>volume</c> (an event carrying only an absolute level) is logged only.
/// Design note Q11 found that receivers report their volume through a separate
/// channel, not the event plist; the engine already reads the receiver's volume
/// from <c>/info</c> every second (<c>device_volume</c>), and the level's unit is
/// unverified, so applying it to the master volume could attenuate twice.</item>
/// </list>
/// </remarks>
public sealed record RemoteCommand(string Name, double? Volume = null, string? Host = null)
{
    /// <summary>Master-volume points one <c>volume_up</c> or <c>volume_down</c> moves.</summary>
    public const int VolumeStep = 5;
    /// <summary>Where AirFlash acts on this command (see the remarks).</summary>
    public RemoteCommandTarget Target => Name switch
    {
        "play" or "pause" or "shuffle_next" or "repeat_next" => RemoteCommandTarget.MediaSession,
        "toggle" or "stop" or "next" or "previous" => RemoteCommandTarget.MediaKey,
        "mute" => RemoteCommandTarget.Mute,
        "volume_up" or "volume_down" => RemoteCommandTarget.MasterVolume,
        _ => RemoteCommandTarget.None,
    };
    /// <summary>Master-volume change for <see cref="RemoteCommandTarget.MasterVolume"/>; otherwise 0.</summary>
    public int VolumeDelta => Name switch { "volume_up" => VolumeStep, "volume_down" => -VolumeStep, _ => 0 };
    /// <summary>Virtual-key code for <c>SendInput</c>, or null when the command is not
    /// sent as a media key (see <see cref="Target"/>).</summary>
    public int? VirtualKey => Name switch
    {
        "toggle" => 0xB3,                       // VK_MEDIA_PLAY_PAUSE
        "stop" => 0xB2,                          // VK_MEDIA_STOP
        "next" => 0xB0,                         // VK_MEDIA_NEXT_TRACK
        "previous" => 0xB1,                      // VK_MEDIA_PREV_TRACK
        _ => null,
    };
    /// <summary>One <see cref="RemoteCommand"/> per string in the event's <c>commands</c>
    /// array, each carrying the event's <c>host</c> and <c>volume</c>. An event with
    /// no command but a finite <c>volume</c> in 0..1 (the engine's normalised range)
    /// yields a single <c>volume</c> command.</summary>
    public static IReadOnlyList<RemoteCommand> Parse(System.Text.Json.JsonElement item)
    {
        var host = item.TryGetProperty("host", out var hostValue) && hostValue.ValueKind == System.Text.Json.JsonValueKind.String ? hostValue.GetString() : null;
        var volume = item.Number("volume");
        var parsed = item.TryGetProperty("commands", out var commands) && commands.ValueKind == System.Text.Json.JsonValueKind.Array
            ? commands.EnumerateArray()
                .Where(command => command.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(command => new RemoteCommand(command.GetString()!, volume, host))
                .ToArray()
            : [];
        if (parsed.Length == 0 && volume is { } level && double.IsFinite(level) && level is >= 0 and <= 1)
            return [new RemoteCommand("volume", level, host)];
        return parsed;
    }
}
