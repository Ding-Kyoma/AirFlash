using System.Text.Json;
using AirFlash.Core;
namespace AirFlash.App.Services;

// The native catalogue shares process identity and root selection with capture.
// Each refresh uses a short-lived IPC connection; no audio or receiver is opened.
public sealed class ApplicationCatalog(IEngineFactory factory) : IApplicationCatalog
{
    public bool Supported { get; private set; } = WindowsCompatibility.SupportsApplicationCapture(Environment.OSVersion.Version);
    public async Task<IReadOnlyList<CaptureApplication>> RefreshAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var connection = factory.Open();
        await connection.SendAsync("catalog", "hello", null, timeout.Token).ConfigureAwait(false);
        var hello = await connection.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException(L.Get("The engine did not return hello."));
        if (hello.Text("event") != "hello" || hello.Number("version") != 1) throw new IOException(L.Get("Incompatible engine protocol version."));
        Supported = EngineHandshake.HasCapability(hello, "application_capture");
        if (!Supported) return [];
        await connection.SendAsync("catalog", "list_applications", null, timeout.Token).ConfigureAwait(false);
        var response = await connection.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException(L.Get("Could not list applications."));
        if (response.Text("event") != "applications") throw new IOException(response.Text("message", L.Get("Could not list applications.")));
        return response.GetProperty("items").Deserialize<CaptureApplication[]>() ?? [];
    }
}
