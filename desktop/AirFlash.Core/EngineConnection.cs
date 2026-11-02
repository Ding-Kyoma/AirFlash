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
    private readonly SemaphoreSlim _reader = new(1, 1);
    private readonly CancellationTokenSource _ioStop = new();
    private readonly object _shutdownGate = new();
    private Task? _disposeTask;
    private long _shutdownStarted;
    private int _disposing;
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(2);
    public ProcessEngineConnection(string executable, Action<string>? log = null) : this(new ProcessStartInfo(executable), log) { }
    internal ProcessEngineConnection(ProcessStartInfo startInfo, Action<string>? log = null)
    {
        _log = log;
        try
        {
            startInfo.UseShellExecute = false; startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true; startInfo.RedirectStandardOutput = true; startInfo.RedirectStandardError = true;
            startInfo.StandardInputEncoding = new UTF8Encoding(false); startInfo.StandardOutputEncoding = Encoding.UTF8; startInfo.StandardErrorEncoding = Encoding.UTF8;
            _process = Process.Start(startInfo) ?? throw new IOException(L.Get("Could not start the audio engine."));
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        if (command == "stop") Interlocked.CompareExchange(ref _shutdownStarted, Stopwatch.GetTimestamp(), 0);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _ioStop.Token);
        if (command == "stop") linked.CancelAfter(ShutdownRemaining());
        var request = JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("N"), session_id = session, command, @params = parameters ?? new { } });
        try
        {
            await _writer.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await _process.StandardInput.WriteLineAsync(request.AsMemory(), linked.Token).ConfigureAwait(false); await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        catch (OperationCanceledException error) when (_ioStop.IsCancellationRequested && !cancellation.IsCancellationRequested)
        { throw new ObjectDisposedException(nameof(ProcessEngineConnection), error.Message); }
    }
    public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _ioStop.Token);
        await _reader.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var line = await _process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length > 65536) throw new IOException(L.Get("The audio engine returned too much data."));
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        finally { _reader.Release(); }
    }
    private TimeSpan ShutdownRemaining() => TimeSpan.FromTicks(Math.Max(0, (ShutdownBudget - Stopwatch.GetElapsedTime(Volatile.Read(ref _shutdownStarted))).Ticks));
    public ValueTask DisposeAsync()
    {
        lock (_shutdownGate)
        {
            if (_disposeTask is null || _disposeTask.IsFaulted)
            {
                Interlocked.CompareExchange(ref _shutdownStarted, Stopwatch.GetTimestamp(), 0);
                Volatile.Write(ref _disposing, 1);
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new(_disposeTask);
        }
    }
    private async Task DrainOutputAsync(CancellationToken cancellation)
    {
        await _reader.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var buffer = new char[4096];
            while (await _process.StandardOutput.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false) > 0) { }
        }
        finally { _reader.Release(); }
    }
    private async Task DisposeCoreAsync()
    {
        using var drainStop = new CancellationTokenSource();
        Task? output = null;
        var exited = false;
        try
        {
            await _ioStop.CancelAsync().ConfigureAwait(false);
            // The session reader has stopped consuming events. Keep draining the
            // pipe so native final metrics cannot block its worker's exit.
            output = DrainOutputAsync(drainStop.Token);
            // Close the pipe without StreamWriter.Close's synchronous flush.
            _process.StandardInput.BaseStream.Close();
            using var timeout = new CancellationTokenSource(ShutdownRemaining());
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!_process.HasExited) { _log?.Invoke("Engine shutdown deadline exceeded; terminating the owned engine process."); _process.Kill(true); }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            exited = true;
            var drains = Task.WhenAll(output, _stderr, _exit);
            _ = drains.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException)
            { _log?.Invoke($"Engine shutdown output drain ended: {error.Message}"); }
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            throw new EngineShutdownException(this, error);
        }
        finally
        {
            await drainStop.CancelAsync().ConfigureAwait(false);
            if (output is not null) _ = output.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (exited)
            {
                // Accessed Process streams are caller-owned. Close them even
                // when output draining reached its own deadline.
                foreach (var stream in new IDisposable[] { _process.StandardInput, _process.StandardOutput, _process.StandardError })
                    try { stream.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                _process.Dispose();
            }
        }
    }
}
internal sealed class EngineShutdownException(ProcessEngineConnection connection, Exception error)
    : IOException(L.Get("The audio engine could not stop. Try disconnecting again."), error)
{
    internal ProcessEngineConnection Connection { get; } = connection;
}
public static class EngineJson
{
    public static string Text(this JsonElement element, string name, string fallback = "") => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    public static long? Integer(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    public static bool? Boolean(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    public static double? Number(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
