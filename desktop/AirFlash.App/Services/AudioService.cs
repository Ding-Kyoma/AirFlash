using System.Collections.Concurrent;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AirFlash.App.Services;

public sealed class AudioService : IAudioService, IMuteRecoveryService, IAsyncDisposable, IMMNotificationClient
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _initialized;
    private readonly MuteRecoveryCoordinator _recovery;
    private readonly Func<IReadOnlyList<AudioEndpoint>> _getEndpoints;
    private readonly Func<string?> _getDefaultEndpoint;
    private MuteRecoverySnapshot _snapshot = MuteRecoverySnapshot.NotInitialized;
    private MMDeviceEnumerator? _notifications;
    private bool _disposed;
    public event Action? EndpointsChanged;
    public event Action? RecoveryChanged;
    public MuteRecoverySnapshot Recovery => Volatile.Read(ref _snapshot);

    public AudioService() : this(new WindowsEndpointBackend(), new FileMuteRecoveryJournal(RecoveryDirectory),
        EnumerateEndpoints, DefaultEndpoint, registerNotifications: true) { }

    // Every endpoint/catalog operation in this overload is injectable. Verification
    // must supply all three boundaries, so it cannot accidentally enumerate COM.
    internal AudioService(IMuteEndpointBackend endpoints, IMuteRecoveryJournal journal,
        Func<IReadOnlyList<AudioEndpoint>> getEndpoints, Func<string?> getDefaultEndpoint, bool registerNotifications = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(journal);
        _getEndpoints = getEndpoints ?? throw new ArgumentNullException(nameof(getEndpoints));
        _getDefaultEndpoint = getDefaultEndpoint ?? throw new ArgumentNullException(nameof(getDefaultEndpoint));
        _recovery = new(endpoints, journal);
        _recovery.Changed += PublishRecovery;
        var thread = new Thread(() =>
        {
            try { foreach (var operation in _queue.GetConsumingEnumerable()) operation(); }
            finally { _queue.Dispose(); _stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "AirFlash audio devices" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        _initialized = InvokeAsync(() =>
        {
            if (registerNotifications)
            {
                _notifications = new MMDeviceEnumerator();
                _notifications.RegisterEndpointNotificationCallback(this);
            }
            return true;
        });
    }
    internal static string RecoveryDirectory
    {
        get
        {
            var regularData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.Name);
            return Path.GetFullPath(AppPaths.DataDirectory).Equals(Path.GetFullPath(regularData), StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.Name, "mute-recovery")
                : Path.Combine(AppPaths.DataDirectory, "mute-recovery");
        }
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
    public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => RunAsync(() =>
    {
        UiPerformance.Count("audio.enumerate");
        return _getEndpoints();
    });
    public Task<string?> GetDefaultEndpointIdAsync() => RunAsync(_getDefaultEndpoint);
    private static IReadOnlyList<AudioEndpoint> EnumerateEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device) endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture ? AudioEndpointKind.Recording : AudioEndpointKind.Playback));
        return endpoints;
    }
    private static string? DefaultEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); return device.ID; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }
    private Task RecoveryOperation(Action action) => RunAsync(() =>
    {
        try { action(); }
        finally { PublishRecovery(); }
        return true;
    });
    public Task InitializeRecoveryAsync() => RecoveryOperation(_recovery.Initialize);
    public Task RefreshRecoveryAsync() => RecoveryOperation(_recovery.Refresh);
    public Task ResolveRecoveryAsync(long revision, Guid owner, long generation, string endpointId, MuteRecoveryChoice choice)
        => RecoveryOperation(() => _recovery.Resolve(revision, owner, generation, endpointId, choice));
    public Task ArchiveUnrecognizedRecoveryAsync(long revision) => RecoveryOperation(() => _recovery.ArchiveUnrecognized(revision));
    public Task MuteAsync(string? endpointId) => RecoveryOperation(() => _recovery.Mute(endpointId));
    public Task RestoreAsync() => RecoveryOperation(_recovery.Restore);
    private void PublishRecovery()
    {
        var next = _recovery.Snapshot;
        if (ReferenceEquals(next, Recovery)) return;
        Volatile.Write(ref _snapshot, next);
        if (RecoveryChanged is { } changed)
            foreach (Action observer in changed.GetInvocationList())
                try { observer(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
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
                    try { _recovery.Restore(); }
                    finally
                    {
                        try
                        {
                            if (_notifications is not null)
                                try { _notifications.UnregisterEndpointNotificationCallback(this); }
                                finally { _notifications.Dispose(); }
                        }
                        finally { _recovery.Changed -= PublishRecovery; _recovery.Dispose(); PublishRecovery(); }
                    }
                    return true;
                });
                _disposed = true;
                _queue.CompleteAdding();
            }
        }
        try { await cleanup.ConfigureAwait(false); }
        finally { await _stopped.Task.ConfigureAwait(false); }
    }

    private sealed class WindowsEndpointBackend : IMuteEndpointBackend
    {
        public IMuteEndpoint Open(string? endpointId)
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                var device = endpointId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : enumerator.GetDevice(endpointId);
                return new WindowsEndpoint(enumerator, device);
            }
            catch { enumerator.Dispose(); throw; }
        }
    }
    private sealed class WindowsEndpoint(MMDeviceEnumerator enumerator, MMDevice device) : IMuteEndpoint
    {
        public string Id => device.ID;
        public AudioEndpointKind Kind => device.DataFlow switch
        {
            DataFlow.Render => AudioEndpointKind.Playback,
            DataFlow.Capture => AudioEndpointKind.Recording,
            _ => throw new InvalidOperationException("The opened audio endpoint has an unsupported direction.")
        };
        public bool Muted { get => device.AudioEndpointVolume.Mute; set => device.AudioEndpointVolume.Mute = value; }
        public void Dispose() { try { device.Dispose(); } finally { enumerator.Dispose(); } }
    }
}
