using System.Text.Json;
namespace AirFlash.Core;

// A command process owns its empty-correlated terminal rejection. Ordinary events
// still belong to their session; missing fields must not match through defaults.
internal static class EngineEvents
{
    public static bool IsProcessRejection(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && item.Number("version") == 1 && item.Text("event") == "error"
        && item.Text("code") == "command_error" && item.Text("channel") == "ipc"
        && item.Boolean("retryable") == false
        && item.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Null
        && EmptyString(item, "id") && EmptyString(item, "session_id");
    private static bool EmptyString(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() == "";

    public static async ValueTask<JsonElement?> ReadAsync(IEngineConnection connection, CancellationToken cancellation)
    {
        JsonElement? item;
        try { item = await connection.ReadAsync(cancellation).ConfigureAwait(false); }
        catch (JsonException error) { throw InvalidEvent(error.Message); }
        if (item is { ValueKind: not JsonValueKind.Object })
            throw InvalidEvent(L.Get("Incompatible engine protocol version."));
        return item;
    }
    private static EngineFailure InvalidEvent(string detail) => new(new("protocol_error", "", "ipc", detail, false));
}

internal sealed class EngineFailure(EngineNotice notice) : IOException(notice.Detail)
{
    public EngineNotice Notice { get; } = notice;
}
