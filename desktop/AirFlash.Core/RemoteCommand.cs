namespace AirFlash.Core;

/// <summary>
/// A command the receiver sent back over the event channel: the speaker's own
/// buttons, or a remote that talks to it.
/// </summary>
/// <remarks>
/// The engine already answers the acknowledgement, so nothing here has to reply.
/// AirFlash's job is to act on the command in the media app the user is playing
/// from, which on Windows means synthesising the matching media key: the reference
/// implementation does the same instead of reaching for the MediaRemote COM API.
/// </remarks>
public sealed record RemoteCommand(string Name, double? Volume = null, string? Host = null)
{
    /// <summary>Virtual-key code for `SendInput`, or null when the command has no
    /// equivalent media key (volume and mute are handled by the app, not the OS).</summary>
    public int? VirtualKey => Name switch
    {
        "play" or "pause" or "toggle" => 0xB3,   // VK_MEDIA_PLAY_PAUSE
        "stop" => 0xB2,                          // VK_MEDIA_STOP
        "next" or "shuffle_next" or "repeat_next" => 0xB0, // VK_MEDIA_NEXT_TRACK
        "previous" => 0xB1,                      // VK_MEDIA_PREV_TRACK
        _ => null,
    };
    public static IReadOnlyList<RemoteCommand> Parse(System.Text.Json.JsonElement item)
    {
        var host = item.TryGetProperty("host", out var hostValue) && hostValue.ValueKind == System.Text.Json.JsonValueKind.String ? hostValue.GetString() : null;
        var volume = item.Number("volume");
        if (!item.TryGetProperty("commands", out var commands) || commands.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        return commands.EnumerateArray()
            .Where(command => command.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(command => new RemoteCommand(command.GetString()!, volume, host))
            .ToArray();
    }
}
