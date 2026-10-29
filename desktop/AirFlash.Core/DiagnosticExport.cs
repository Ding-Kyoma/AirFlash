using System.Text.Json;
namespace AirFlash.Core;

/// <summary>
/// Collects a diagnostic bundle that can be attached to an issue without leaking
/// the user's network or file system.
/// </summary>
/// <remarks>
/// The engine already redacts its own snapshot: hosts and paths become stable
/// pseudonyms and credential-looking fields are dropped. This class adds the parts
/// only the desktop knows — app version, settings that describe the setup rather
/// than the user, and the tail of the log — and writes one JSON file.
/// </remarks>
public static class DiagnosticExport
{
    /// <summary>Ask a running engine for its redacted session snapshot. Returns null
    /// when no engine is running; a diagnostics export must never fail because of
    /// that.</summary>
    public static async Task<JsonElement?> SnapshotAsync(IEngineFactory factory, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = factory.Open();
            await connection.SendAsync("diagnostics", "hello", null, timeout.Token).ConfigureAwait(false);
            _ = await EngineEvents.ReadAsync(connection, timeout.Token).ConfigureAwait(false);
            await connection.SendAsync("diagnostics", "diag_summary", null, timeout.Token).ConfigureAwait(false);
            var response = await EngineEvents.ReadAsync(connection, timeout.Token).ConfigureAwait(false);
            return response is { } value && value.Text("event") == "diag_summary" ? value.Clone() : null;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Build the bundle. <paramref name="logTail"/> is truncated by the
    /// caller; nothing here rewrites it, so a log that already contains an address
    /// would still leak it — the engine's redaction only covers its own snapshot.</summary>
    public static string Compose(string appVersion, string engineVersion, JsonElement? snapshot, IEnumerable<string>? logTail = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("generated_at", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("app_version", appVersion);
            writer.WriteString("engine_version", engineVersion);
            writer.WriteString("os", Environment.OSVersion.VersionString);
            writer.WriteBoolean("os_64bit", Environment.Is64BitOperatingSystem);
            writer.WriteNumber("processor_count", Environment.ProcessorCount);
            if (snapshot is { } session) { writer.WritePropertyName("session"); session.WriteTo(writer); }
            if (logTail is { } lines)
            {
                writer.WriteStartArray("log_tail");
                foreach (var line in lines) writer.WriteStringValue(line);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Write(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"AirFlash-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
