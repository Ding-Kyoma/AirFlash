using System.Globalization;
namespace AirFlash.Core;

/// <summary>
/// The desktop half of a streaming session summary, keyed by the same
/// <c>session_id</c> the engine receives on the wire.
/// </summary>
/// <param name="SessionId">The IPC session id (<c>Guid.NewGuid().ToString("N")</c>). The
/// engine appends <c>-&lt;session_epoch&gt;</c> to it in its own summary row, so a
/// join is a prefix match; one desktop row can pair with several engine rows when
/// the engine resumed inside one IPC session.</param>
/// <param name="DesktopReconnects">How many reconnects this playback run had already
/// performed before this connection attempt started.</param>
/// <param name="DesktopReconnectReason">Why the most recent reconnect happened:
/// <c>transport_fault:&lt;engine code&gt;</c>, or <c>none</c> for the first attempt.</param>
public sealed record DesktopSessionRecord(string SessionId, int DesktopReconnects, string DesktopReconnectReason);

/// <summary>
/// Appends the desktop-owned columns next to the engine's <c>session-summary.csv</c>
/// under <c>%APPDATA%\AirFlash\logs\</c>. The engine cannot see a desktop reconnect:
/// when the transport fails, the desktop opens a new IPC session rather than
/// repairing the engine's. Recording that here keeps the two facts joined by
/// <c>session_id</c> instead of asking the engine to guess.
/// </summary>
/// <remarks>
/// Only the opaque session id and fixed reason labels are written; no host, path,
/// endpoint or receiver identity ever reaches the file. Every I/O failure is
/// swallowed for the same reason as the engine's summary row: diagnostics must
/// never break a session.
/// </remarks>
public static class DesktopSessionLog
{
    public const string FileName = "session-desktop.csv";
    public const string Header = "session_id,desktop_reconnects,desktop_reconnect_reason";
    public const string NoReconnect = "none";
    private static readonly object Gate = new();

    /// <summary>Beside the engine file, which uses the same `%APPDATA%\AirFlash\logs`
    /// root (see the engine's <c>telemetry::summary_path</c>).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AirFlash", "logs", FileName);

    public static void Append(DesktopSessionRecord record) => Append(DefaultPath, record);

    /// <summary>Append one row, writing the header first when the file is new.</summary>
    public static void Append(string path, DesktopSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = string.Join(',',
            Field(record.SessionId),
            record.DesktopReconnects.ToString(CultureInfo.InvariantCulture),
            Field(record.DesktopReconnectReason));
        try
        {
            lock (Gate)
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
                var newFile = !File.Exists(path) || new FileInfo(path).Length == 0;
                File.AppendAllText(path, (newFile ? Header + Environment.NewLine : "") + line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Field(string value) => value.IndexOfAny([',', '"', '\n', '\r']) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
}
