using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
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
    private readonly StringBuilder _errorText = new();
    private readonly Task _exit;
    public ProcessEngineConnection(string executable, Action<string>? log = null)
    {
        _log = log;
        try
        {
            _process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }) ?? throw new IOException(L.Get("Could not start the audio engine."));
        }
        catch (Win32Exception error)
        {
            _log?.Invoke($"Engine process-start failed win32={error.NativeErrorCode} hresult=0x{error.HResult:X8}");
            throw new IOException($"{L.Get("Could not start the audio engine.")} Win32 {error.NativeErrorCode}: {error.Message}", error);
        }
        _stderr = DrainErrorAsync();
        _exit = ObserveExitAsync();
    }
    private async Task DrainErrorAsync()
    {
        var buffer = new char[1024];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            lock (_errorText) { if (_errorText.Length < 4096) _errorText.Append(buffer, 0, Math.Min(count, 4096 - _errorText.Length)); }
        string text;
        lock (_errorText) text = _errorText.ToString();
        // Native panic text is bounded, and labelled secrets must never enter host logs.
        text = DiagnosticText.Protect(text);
        if (text.Length > 0) _log?.Invoke("Engine stderr (first 4096 characters): " + text);
    }
    private async Task ObserveExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        _log?.Invoke($"Engine process-exit code={_process.ExitCode} hex=0x{unchecked((uint)_process.ExitCode):X8}");
    }
    public string FailureDescription => _process.HasExited
        ? $"{L.Get("The engine did not return hello.")} Exit code {_process.ExitCode} (0x{unchecked((uint)_process.ExitCode):X8})."
        : L.Get("The engine did not return hello.");
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
            await _exit.ConfigureAwait(false);
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
