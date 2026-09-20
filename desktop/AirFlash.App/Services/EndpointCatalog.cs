using System.Windows.Threading;
using AirFlash.Core;
namespace AirFlash.App.Services;

public sealed class EndpointCatalog : IDisposable
{
    private readonly IAudioService _audio;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Task<IReadOnlyList<AudioEndpoint>>? _refresh;
    private Task _notifications = Task.CompletedTask;
    private bool _notificationPending;
    private long _generation;
    private long _refreshedGeneration = -1;
    private bool _disposed;
    public IReadOnlyList<AudioEndpoint>? Items { get; private set; }
    public event Action? Changed;
    public event Func<Task>? DevicesChanged;
    public event Action<Exception>? Failed;
    public EndpointCatalog(IAudioService audio, Dispatcher dispatcher)
    {
        _audio = audio; _dispatcher = dispatcher;
        _timer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            _timer!.Stop();
            _notificationPending = true;
            if (_notifications.IsCompleted) _notifications = ProcessNotificationsAsync();
        }, dispatcher);
        _timer.Stop(); _audio.EndpointsChanged += OnChanged;
    }
    private async Task ProcessNotificationsAsync()
    {
        do
        {
            _notificationPending = false;
            try
            {
                if (_refreshedGeneration != _generation) await RefreshAsync();
                if (!_disposed && DevicesChanged is { } changed) await changed();
            }
            catch (Exception error) { if (!_disposed) Failed?.Invoke(error); }
        } while (!_disposed && _notificationPending);
    }
    private void OnChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _generation++;
        _timer.Stop(); _timer.Start();
    }, DispatcherPriority.Background);
    public Task<IReadOnlyList<AudioEndpoint>> GetAsync() => Items is null ? RefreshAsync() : Task.FromResult(Items);
    public Task<IReadOnlyList<AudioEndpoint>> RefreshAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return Task.FromException<IReadOnlyList<AudioEndpoint>>(new ObjectDisposedException(nameof(EndpointCatalog)));
        if (_refresh is not null) return _refresh;
        var completion = new TaskCompletionSource<IReadOnlyList<AudioEndpoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refresh = completion.Task;
        _ = RefreshCoreAsync(completion);
        return completion.Task;
    }
    private async Task RefreshCoreAsync(TaskCompletionSource<IReadOnlyList<AudioEndpoint>> completion)
    {
        try
        {
            IReadOnlyList<AudioEndpoint> items;
            long generation;
            do
            {
                generation = _generation;
                items = (await _audio.GetEndpointsAsync()).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            } while (!_disposed && generation != _generation);
            if (!_disposed)
            {
                _refreshedGeneration = generation;
                if (Items is null || !Items.SequenceEqual(items)) { Items = items; Changed?.Invoke(); }
            }
            completion.TrySetResult(items);
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { _refresh = null; }
    }
    public Task DrainAsync() => Task.WhenAll(_refresh ?? Task.CompletedTask, _notifications);
    public void Dispose() { _disposed = true; _timer.Stop(); _audio.EndpointsChanged -= OnChanged; }
}
