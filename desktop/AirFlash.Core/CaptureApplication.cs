using System.Text.Json.Serialization;
namespace AirFlash.Core;

public sealed record CaptureApplication(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("process_id")] uint ProcessId,
    [property: JsonPropertyName("started_at")] ulong StartedAt)
{
    public string DisplayName => ProcessId == 0 ? Name : $"{Name} · PID {ProcessId}";
}

public interface IApplicationCatalog
{
    bool Supported { get; }
    Task<IReadOnlyList<CaptureApplication>> RefreshAsync(CancellationToken cancellation = default);
}
