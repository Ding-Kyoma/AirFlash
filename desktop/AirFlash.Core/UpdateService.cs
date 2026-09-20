using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AirFlash.Core;

public sealed record ReleaseUpdate(Version Version, Uri DownloadPage);

public sealed class UpdateService(HttpClient client)
{
    public const string RepositoryUrl = "https://github.com/Ding-Kyoma/AirFlash";
    public const string LatestApi = "https://api.github.com/repos/Ding-Kyoma/AirFlash/releases/latest";
    public async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.ParseAdd("AirFlash-update-check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("Invalid release version.");
        // Construct the destination from the trusted repository and validated tag, never from API HTML.
        return new(version, new Uri($"{RepositoryUrl}/releases/tag/{tag}"));
    }
}

public sealed class UpdateCheckState(UpdateService service, Version currentVersion) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private bool _busy, _disposed;
    private string _status = "";
    private Uri? _downloadPage;
    public bool IsChecking { get => _busy; private set => Set(ref _busy, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Uri? DownloadPage { get => _downloadPage; private set { Set(ref _downloadPage, value); Notify(nameof(HasUpdate)); } }
    public bool HasUpdate => DownloadPage is not null;
    public async Task CheckAsync()
    {
        if (_disposed || IsChecking) return;
        IsChecking = true; DownloadPage = null; Status = L.Get("Checking for updates…");
        try
        {
            var release = await service.CheckAsync(_stop.Token);
            if (_disposed) return;
            if (release is null) Status = L.Get("No stable release is available yet.");
            else if (release.Version <= currentVersion) Status = L.Get("You are up to date.");
            else { DownloadPage = release.DownloadPage; Status = string.Format(L.Get("AirFlash {0} is available."), release.Version.ToString(3)); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { if (!_disposed) Status = L.Get("Update check timed out. Please try again."); }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        { if (!_disposed) Status = L.Get("Could not check for updates. Please try again later."); }
        finally { IsChecking = false; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); }
}
