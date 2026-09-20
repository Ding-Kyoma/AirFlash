using System.Collections.Concurrent;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AirFlash.App.Services;

public sealed class AudioService : IAudioService, IAsyncDisposable, IMMNotificationClient
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, bool> _originalMute = [];
    private readonly Task _initialized;
    private MMDeviceEnumerator? _notifications;
    private bool _disposed;
    public event Action? EndpointsChanged;

    public AudioService()
    {
        var thread = new Thread(() =>
        {
            try { foreach (var operation in _queue.GetConsumingEnumerable()) operation(); }
            finally { _queue.Dispose(); _stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "AirFlash audio devices" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        _initialized = InvokeAsync(() =>
        {
            _notifications = new MMDeviceEnumerator();
            _notifications.RegisterEndpointNotificationCallback(this);
            return true;
        });
    }
    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue)
        {
            if (_disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(AudioService)));
            _queue.Add(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } });
        }
        return result.Task;
    }
    private async Task<T> RunAsync<T>(Func<T> action)
    {
        await _initialized.ConfigureAwait(false);
        return await InvokeAsync(action).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => RunAsync<IReadOnlyList<AudioEndpoint>>(() =>
    {
        UiPerformance.Count("audio.enumerate");
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) endpoints.Add(new(device.ID, device.FriendlyName));
        return endpoints;
    });
    public Task<string?> GetDefaultEndpointIdAsync() => RunAsync(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); return device.ID; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    });
    public Task MuteAsync(string? endpointId) => RunAsync(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = endpointId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : enumerator.GetDevice(endpointId);
        if (_originalMute.ContainsKey(device.ID)) return true;
        var previous = device.AudioEndpointVolume.Mute;
        device.AudioEndpointVolume.Mute = true;
        _originalMute[device.ID] = previous;
        return true;
    });
    public Task RestoreAsync() => RunAsync(() => { Restore(); return true; });
    private void Restore()
    {
        if (_originalMute.Count == 0) return;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var (id, previous) in _originalMute.ToArray())
            try { using var device = enumerator.GetDevice(id); device.AudioEndpointVolume.Mute = previous; _originalMute.Remove(id); }
            catch (System.Runtime.InteropServices.COMException) { /* Retry when the endpoint returns. */ }
    }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render && role == Role.Console) EndpointsChanged?.Invoke(); }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => EndpointsChanged?.Invoke();
    public void OnDeviceAdded(string deviceId) => EndpointsChanged?.Invoke();
    public void OnDeviceRemoved(string deviceId) => EndpointsChanged?.Invoke();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => EndpointsChanged?.Invoke();
    public async ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (_queue)
        {
            if (_disposed) { cleanup = _stopped.Task; }
            else
            {
                cleanup = InvokeAsync(() =>
                {
                    try { Restore(); }
                    finally { if (_notifications is not null) { _notifications.UnregisterEndpointNotificationCallback(this); _notifications.Dispose(); } }
                    return true;
                });
                _disposed = true;
                _queue.CompleteAdding();
            }
        }
        try { await cleanup.ConfigureAwait(false); }
        finally { await _stopped.Task.ConfigureAwait(false); }
    }
}
