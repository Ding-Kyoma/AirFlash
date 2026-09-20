using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
namespace AirFlash.Core;

public interface IEngineConnection : IAsyncDisposable
{
    Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation);
    ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation);
}
public interface IEngineFactory { IEngineConnection Open(); }
public sealed class ProcessEngineFactory(string executable, Action<string>? log = null) : IEngineFactory
{
    public IEngineConnection Open() => new ProcessEngineConnection(executable, log);
}
public sealed class ProcessEngineConnection : IEngineConnection
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Action<string>? _log;
    private readonly Task _stderr;
    public ProcessEngineConnection(string executable, Action<string>? log = null)
    {
        _log = log;
        _process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }) ?? throw new IOException(L.Get("Could not start the audio engine."));
        _stderr = DrainErrorAsync();
    }
    private async Task DrainErrorAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) _log?.Invoke(line);
    }
    public async Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
    {
        var request = JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("N"), session_id = session, command, @params = parameters ?? new { } });
        await _writer.WaitAsync(cancellation).ConfigureAwait(false);
        try { await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellation).ConfigureAwait(false); await _process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }
    public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
    {
        var line = await _process.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false);
        if (line is null) return null;
        if (line.Length > 65536) throw new IOException(L.Get("The audio engine returned too much data."));
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(true); await _process.WaitForExitAsync().ConfigureAwait(false); }
            await _stderr.ConfigureAwait(false);
        }
        finally { _process.Dispose(); _writer.Dispose(); }
    }
}
public static class EngineJson
{
    public static string Text(this JsonElement element, string name, string fallback = "") => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    public static long? Integer(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    public static bool? Boolean(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    public static double? Number(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
