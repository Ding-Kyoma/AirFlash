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
    /// <summary>Request a redacted snapshot of the current stream. After playback
    /// stops, return the last accepted snapshot if available. Returns null when
    /// the controller has no snapshot for the request.</summary>
    public static Task<JsonElement?> SnapshotAsync(SessionController controller, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.DiagnosticSnapshotAsync(cancellation);
    }

    /// <summary>Capture the current summary together with its frozen counter history.</summary>
    public static Task<DiagnosticCapture> CaptureAsync(SessionController controller, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.CaptureDiagnosticsAsync(cancellation);
    }

    /// <summary>Build a bundle from runtime information and a redacted snapshot.</summary>
    public static string Compose(string appVersion, string engineVersion, JsonElement? snapshot)
        => Compose(appVersion, engineVersion, snapshot, null, null);

    /// <summary>Build a bundle from runtime information, a redacted snapshot, and
    /// optional recent numeric counter changes.</summary>
    public static string Compose(string appVersion, string engineVersion, JsonElement? snapshot,
        IReadOnlyList<PlaybackCounterDelta>? counterHistory = null, DiscoveryDiagnosticsSnapshot? discovery = null)
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
            if (counterHistory is not null)
            {
                writer.WriteStartArray("counter_delta_history");
                foreach (var report in counterHistory.TakeLast(PlaybackCounterHistory.Capacity))
                {
                    if (report.Source is not ("capture_metrics" or "transport_metrics")) continue;
                    var counters = SelectCounters(report.Counters, report.Source == "capture_metrics"
                        ? ["underrun_packets", "padded_frames", "fully_padded_packets", "dropped_frames"]
                        : ["sender_late_recoveries", "skipped_packets"]);
                    // Keep evidence bounded independently from group admission.
                    // Only ordinal numbers and known counters enter the saved history.
                    var members = report.Source == "transport_metrics" ? report.Members?
                        .Where(member => member.Member is >= 1 and <= PlaybackCounterHistory.MemberCapacity)
                        .DistinctBy(member => member.Member).Take(PlaybackCounterHistory.MemberCapacity)
                        .Select(member => new MemberCounterDelta(member.Member, SelectCounters(member.Counters,
                            ["retransmit_requests", "retransmits_sent", "retransmit_expired", "retransmit_missing",
                                "retransmit_queue_drops", "media_send_errors", "sync_send_errors"])))
                        .Where(member => member.Counters.Count > 0).ToArray() : null;
                    if (counters.Count == 0 && (members is null || members.Length == 0)) continue;
                    JsonSerializer.Serialize(writer, report with { Counters = counters, Members = members }, AppSettings.JsonOptions);
                }
                writer.WriteEndArray();
            }
            if (discovery is not null)
            {
                const DiscoveryDemand allowed = DiscoveryDemand.Panel | DiscoveryDemand.Receivers | DiscoveryDemand.Startup | DiscoveryDemand.SoftwareStereo;
                // Treat optional providers as an input boundary. Only typed state
                // and fixed-format pseudonyms enter this shareable export.
                var history = (discovery.History ?? []).TakeLast(DiscoveryDiagnosticsSnapshot.HistoryCapacity)
                    .Where(entry => Enum.IsDefined(entry.Event) && Enum.IsDefined(entry.Reason) && Enum.IsDefined(entry.ServiceType))
                    .Select(entry => entry with
                    {
                        Demand = entry.Demand & allowed,
                        ServiceToken = SafeToken(entry.ServiceToken), DeviceToken = SafeToken(entry.DeviceToken),
                        StereoToken = SafeToken(entry.StereoToken), EndpointToken = SafeToken(entry.EndpointToken)
                    }).ToArray();
                writer.WritePropertyName("discovery");
                JsonSerializer.Serialize(writer, discovery with { Demand = discovery.Demand & allowed, History = history }, AppSettings.JsonOptions);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? SafeToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit) ? token : null;

    private static IReadOnlyDictionary<string, ulong> SelectCounters(IReadOnlyDictionary<string, ulong>? counters, string[] names)
    {
        var selected = new Dictionary<string, ulong>();
        foreach (var name in names)
            if (counters?.TryGetValue(name, out var value) == true && value > 0) selected.Add(name, value);
        return selected;
    }

    public static string Write(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"AirFlash-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
