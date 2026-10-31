using System.Text.Json;
namespace AirFlash.Core;

/// <summary>
/// Collects a diagnostic bundle that can be attached to an issue without leaking
/// the user's network or file system.
/// </summary>
/// <remarks>
/// The engine already redacts its own snapshot: hosts and paths become stable
/// pseudonyms and credential-looking fields are dropped. This class adds the parts
/// only the desktop knows — app version and runtime information — and writes one
/// JSON file. Raw application logs are not part of the export.
/// </remarks>
public static class DiagnosticExport
{
    /// <summary>Ask a running engine for its redacted session snapshot. Returns null
    /// when no engine is running; a diagnostics export must never fail because of
    /// that.</summary>
    public static Task<JsonElement?> SnapshotAsync(SessionController controller, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.DiagnosticSnapshotAsync(cancellation);
    }

    /// <summary>Build a bundle from runtime information and a redacted snapshot.</summary>
    public static string Compose(string appVersion, string engineVersion, JsonElement? snapshot)
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
