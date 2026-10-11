namespace AirFlash.Core;

public enum EngineAvailability { Initializing, Ready, Failed }

// One bounded probe at a time. Shutdown invalidates late completions before disposal.
public sealed class EngineInitialization(Func<CancellationToken, Task<string>> probe, Action<string>? log = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _attempt = Task.CompletedTask;
    private bool _disposed;
    public EngineAvailability State { get; private set; } = EngineAvailability.Initializing;
    public string Version { get; private set; } = "";
    public string Error { get; private set; } = "";
    public event Action? Changed;

    public Task InitializeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_attempt.IsCompleted) return _attempt;
            State = EngineAvailability.Initializing; Error = "";
            Changed?.Invoke();
            _attempt = Task.Run(async () =>
            {
                try
                {
                    log?.Invoke("Startup stage=engine-probe started");
                    var version = await probe(_stop.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_disposed) return;
                        Version = version; State = EngineAvailability.Ready;
                    }
                    log?.Invoke("Startup stage=engine-probe ready");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    log?.Invoke($"Startup stage=engine-probe failed {error}");
                    lock (_sync)
                    {
                        if (_disposed) return;
                        State = EngineAvailability.Failed; Error = error.Message;
                    }
                }
                Changed?.Invoke();
            });
            return _attempt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task attempt;
        lock (_sync) { if (_disposed) return; _disposed = true; attempt = _attempt; }
        await _stop.CancelAsync().ConfigureAwait(false);
        await attempt.ConfigureAwait(false);
        _stop.Dispose();
    }
}
