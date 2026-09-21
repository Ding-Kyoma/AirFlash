using System.Collections.ObjectModel;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.ViewModels;

public sealed class AppViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly IDiscoveryService _discovery;
    private readonly IAutostart _autostart;
    private readonly IAudioService _audio;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly Dictionary<string, Receiver> _known = [];
    private readonly HashSet<string> _autoAttempted = [];
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _autoTimer;
    private AppSettings _settings;
    private SessionSnapshot _snapshot = new(PlaybackState.Idle);
    private string _notice = "";
    private bool _volumeDirty, _autoSuppressed, _closing;
    private string? _defaultEndpoint;
    private long _editRevision;
    private Task _startup = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _monitorVisible;
    private readonly Dictionary<string, string> _monitorValues = [];
    public EndpointCatalog Endpoints { get; }
    public bool IsClosing => _closing;
    public SessionController Session { get; }
    public ObservableCollection<ReceiverViewModel> Receivers { get; } = [];
    public IReadOnlyList<Receiver> AllReceivers => _known.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public AppSettings Settings => _settings;
    public SessionSnapshot Snapshot => _snapshot;
    public string Notice { get => _notice; private set { Set(ref _notice, value); Notify(nameof(ErrorDetails)); } }
    public int MasterVolume { get => _settings.MasterVolume; set { value = Math.Clamp(value, 0, 100); if (value == _settings.MasterVolume) return; _settings.MasterVolume = value; VolumeChanged(); Notify(); } }
    public bool Muted => Session.Muted;
    public string MuteLabel => Muted ? L.Get("Unmute") : L.Get("Mute");
    public string StatusTitle => _snapshot.State switch { PlaybackState.Connecting => L.Get("Connecting"), PlaybackState.Streaming => L.Get("Playing"), PlaybackState.Standby => L.Get("Standby"), PlaybackState.Pairing => L.Get("Pairing"), PlaybackState.Error => L.Get("Connection error"), _ => L.Get("Ready") };
    public string StatusDetail => _snapshot.Message.Length > 0 ? _snapshot.Message : _snapshot.IsActive ? L.Format("{0} · target {1} ms", _snapshot.Receiver?.Name, _snapshot.TargetLatency) : L.Get("Select a receiver to play system audio");
    public string MonitorLatency => _snapshot.Metrics?.CaptureToSendP95 is { } ms ? $"{ms:F1} ms" : "—";
    public string MonitorQueue => _snapshot.Metrics?.MaxQueueAge is { } ms ? $"{ms:F1} ms" : "—";
    public string MonitorUnderruns => _snapshot.Metrics?.Underruns?.ToString() ?? "—";
    public string MonitorDrops => _snapshot.Metrics?.DroppedFrames?.ToString() ?? "—";
    public string MonitorRate => _snapshot.Metrics is { InputRate: > 0 } m ? $"{m.InputRate:N0} Hz" : "—";
    public string MonitorStreamRate => _snapshot.StreamRate is { } rate and > 0 ? $"{rate:N0} Hz · 16-bit" : "—";
    public string MonitorRecoveries => _snapshot.Diagnostics?.Transport?.SenderLateRecoveries?.ToString() ?? "—";
    public string MonitorSkipped => _snapshot.Diagnostics?.Transport?.SkippedPackets?.ToString() ?? "—";
    public string MonitorReconnects => _snapshot.Diagnostics?.ReconnectCount.ToString() ?? "—";
    public string MonitorSessionUptime => _snapshot.Diagnostics?.Transport?.SessionUptimeMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"d\.hh\:mm\:ss") : "—";
    public string MonitorWarnings => _snapshot.Diagnostics?.Warnings is { Count: > 0 } warnings ? string.Join("\n", warnings.Select(w => w.Detail)) : "—";
    public string MonitorLastFault => _snapshot.Diagnostics?.LastFault?.Detail ?? "—";
    public ObservableCollection<TransportMemberViewModel> MonitorMembers { get; } = [];
    public string MonitorBeforeFault
    {
        get
        {
            if (_snapshot.Diagnostics is not { LastFault: not null } diagnostics) return "—";
            var capture = diagnostics.CaptureBeforeFault;
            var summary = L.Format("Local underruns: {0}; local drops: {1}; local p95: {2}", capture?.Underruns?.ToString() ?? "—", capture?.DroppedFrames?.ToString() ?? "—", capture?.CaptureToSendP95 is { } ms ? $"{ms:F1} ms" : "—");
            if (diagnostics.TransportBeforeFault is { } transport)
            {
                summary += "\n" + L.Format("Sender recoveries: {0}; skipped packets: {1}", transport.SenderLateRecoveries?.ToString() ?? "—", transport.SkippedPackets?.ToString() ?? "—");
                summary += "\n" + string.Join("\n", transport.Members.Select(m => m.Host + "\n" + new TransportMemberViewModel(m).Summary));
            }
            return summary;
        }
    }
    public string ErrorDetails => _snapshot.State == PlaybackState.Error ? _snapshot.Message : Notice;
    public string EngineVersion { get; set; } = L.Get("Unknown");
    public string LatencyMode { get => _settings.LatencyMode; set { if (value == _settings.LatencyMode || value is null) return; _settings.LatencyMode = value; VolumeChanged(); Notify(); } }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand MuteCommand { get; }
    public event Action? SettingsChanged;
    public event Action? CatalogChanged;
    public AppViewModel(ISettingsStore store, IDiscoveryService discovery, IAutostart autostart, IAudioService audio, IEngineFactory factory, Dispatcher dispatcher)
    {
        _store = store; _discovery = discovery; _autostart = autostart; _audio = audio; _dispatcher = dispatcher;
        Endpoints = new(audio, dispatcher);
        Endpoints.DevicesChanged += OnEndpointsChangedAsync;
        Endpoints.Failed += ShowError;
        _settings = store.Load();
        Session = new(factory, audio, AppPaths.Log);
        Session.Changed += SessionChanged;
        _discovery.Changed += list => _dispatcher.BeginInvoke(() => OnDiscovered(list));
        _discovery.Failed += message => _dispatcher.BeginInvoke(() => ShowError(new IOException(message)));
        _volumeTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, async (_, _) => { _volumeTimer!.Stop(); try { await FlushVolumeAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _volumeTimer.Stop();
        _autoTimer = new(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) => { _autoTimer!.Stop(); try { await TryAutoConnectAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _autoTimer.Stop();
        StopCommand = new(() => StopAsync(), ShowError, () => _snapshot.IsActive);
        MuteCommand = new(async () => { await Session.SetMutedAsync(!Session.Muted); Notify(nameof(Muted)); Notify(nameof(MuteLabel)); }, ShowError);
        MergeManualReceivers();
    }
    public void Start()
    {
        _startup = StartAsync();
    }
    public Task Startup => _startup;
    private async Task StartAsync()
    {
        await _settingsGate.WaitAsync();
        try
        {
            _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync();
            if (_closing) return;
            await _autostart.SetAsync(_settings.StartAtLogin);
            if (_store is SettingsStore { LoadWarning: { } warning }) Notice = warning;
            await MigrateEndpointNameAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
        if (_closing) return;
        _discovery.Start();
        RefreshReceivers(); ScheduleAutoConnect();
    }
    private async Task MigrateEndpointNameAsync()
    {
        if (_settings.CaptureMode != "endpoint" || string.IsNullOrWhiteSpace(_settings.CaptureEndpoint)) return;
        try
        {
            var selected = _settings.CaptureEndpoint;
            var endpoints = await Endpoints.GetAsync();
            if (_settings.CaptureEndpoint != selected || _closing) return;
            if (endpoints.Any(e => e.Id == _settings.CaptureEndpoint)) return;
            var matches = endpoints.Where(e => e.Name == _settings.CaptureEndpoint).ToArray();
            if (matches.Length == 1) _settings.CaptureEndpoint = matches[0].Id;
            else Notice = L.Get("The previous capture endpoint is unavailable. Select another in Settings → Audio capture.");
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task OnEndpointsChangedAsync()
    {
        if (_closing) return;
        await _settingsGate.WaitAsync();
        try
        {
            if (_closing) return;
            var next = await _audio.GetDefaultEndpointIdAsync();
            if (_closing) return;
            if (_settings.CaptureMode == "loopback" && next != _defaultEndpoint && _snapshot.IsActive && _snapshot.State != PlaybackState.Pairing && _snapshot.Receiver is { } receiver) await Session.StartAsync(receiver, _settings.Clone());
            _defaultEndpoint = next;
            if (!_snapshot.IsActive) await _audio.RestoreAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
    }
    private async void OnDiscovered(IReadOnlyList<Receiver> list)
    {
        if (_closing) return;
        try
        {
            var before = AllReceivers;
            var discovered = list.ToDictionary(r => r.Id);
            foreach (var key in _known.Keys.ToArray()) if (!_known[key].IsManual && !discovered.ContainsKey(key)) _known[key] = _known[key] with { Online = false };
            foreach (var receiver in list)
            {
                if (!_known.TryGetValue(receiver.Id, out var previous) || !previous.Online || !previous.Complete && receiver.Complete) _autoAttempted.Remove(receiver.Id);
                _known[receiver.Id] = receiver;
            }
            MergeManualReceivers();
            if (!CatalogEqual(before, AllReceivers)) { RefreshReceivers(); CatalogChanged?.Invoke(); }
            if (_snapshot.IsActive && _snapshot.Receiver is { IsManual: false } current)
            {
                if (!discovered.TryGetValue(current.Id, out var next) || !next.Complete) await StopAsync(false);
                else if (next.TransportKey != current.TransportKey)
                {
                    await _settingsGate.WaitAsync();
                    try { if (!_closing) await Session.StartAsync(next, _settings.Clone()); }
                    finally { _settingsGate.Release(); }
                }
            }
            ScheduleAutoConnect();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void MergeManualReceivers()
    {
        var ids = _settings.ManualReceivers.Select(r => r.Id).ToHashSet();
        foreach (var key in _known.Where(p => p.Value.IsManual && !ids.Contains(p.Key)).Select(p => p.Key).ToArray()) _known.Remove(key);
        foreach (var manual in _settings.ManualReceivers)
            _known[manual.Id] = new(manual.Id, manual.Name, manual.Host, manual.Port) { IsManual = true };
    }
    private static bool CatalogEqual(IReadOnlyList<Receiver> left, IReadOnlyList<Receiver> right)
        => left.Count == right.Count && left.Zip(right).All(p => ReceiverEqual(p.First, p.Second));
    internal static bool ReceiverEqual(Receiver left, Receiver right)
        => (left with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>() }) == (right with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>() })
            && left.Codecs.SequenceEqual(right.Codecs) && CatalogEqual(left.Members, right.Members);
    private void RefreshReceivers()
    {
        var visible = AllReceivers.Where(r => r.Online && !_settings.ReadOptions(r.Id).Hidden).ToArray();
        foreach (var row in Receivers.Where(row => visible.All(r => r.Id != row.Receiver.Id)).ToArray()) Receivers.Remove(row);
        for (var i = 0; i < visible.Length; i++)
        {
            var row = Receivers.FirstOrDefault(r => r.Receiver.Id == visible[i].Id);
            if (row is null) { row = new(this, visible[i]); Receivers.Insert(i, row); }
            else { row.Receiver = visible[i]; var previous = Receivers.IndexOf(row); if (previous != i) Receivers.Move(previous, i); }
            row.Refresh();
        }
        Notify(nameof(AllReceivers));
    }
    private void SessionChanged(SessionSnapshot snapshot) => _dispatcher.BeginInvoke(() =>
    {
        if (_closing || !ReferenceEquals(snapshot, Session.Snapshot)) return;
        var title = StatusTitle; var detail = StatusDetail; var errors = ErrorDetails;
        var previous = _snapshot;
        _snapshot = snapshot;
        Notify(nameof(Snapshot));
        if (title != StatusTitle) Notify(nameof(StatusTitle));
        if (detail != StatusDetail) Notify(nameof(StatusDetail));
        if (errors != ErrorDetails) Notify(nameof(ErrorDetails));
        if (previous.State != snapshot.State || previous.Receiver?.Id != snapshot.Receiver?.Id)
        {
            foreach (var row in Receivers) row.Refresh();
            StopCommand.Refresh();
        }
        foreach (var row in Receivers) row.Refresh();
        if (_monitorVisible) RefreshMonitor();
    });
    public void SetMonitorVisible(bool visible)
    {
        _monitorVisible = visible;
        if (visible) RefreshMonitor();
    }
    private void RefreshMonitor()
    {
        var values = new Dictionary<string, string>
        {
            [nameof(MonitorLatency)] = MonitorLatency, [nameof(MonitorQueue)] = MonitorQueue,
            [nameof(MonitorUnderruns)] = MonitorUnderruns, [nameof(MonitorDrops)] = MonitorDrops,
            [nameof(MonitorRate)] = MonitorRate, [nameof(MonitorStreamRate)] = MonitorStreamRate,
            [nameof(MonitorRecoveries)] = MonitorRecoveries,
            [nameof(MonitorSkipped)] = MonitorSkipped, [nameof(MonitorReconnects)] = MonitorReconnects,
            [nameof(MonitorSessionUptime)] = MonitorSessionUptime, [nameof(MonitorWarnings)] = MonitorWarnings,
            [nameof(MonitorLastFault)] = MonitorLastFault, [nameof(MonitorBeforeFault)] = MonitorBeforeFault
        };
        foreach (var (name, value) in values)
            if (!_monitorValues.TryGetValue(name, out var previous) || previous != value) { _monitorValues[name] = value; Notify(name); }
        var members = _snapshot.Diagnostics?.Transport?.Members ?? [];
        foreach (var row in MonitorMembers.Where(r => members.All(m => m.Host != r.Host)).ToArray()) MonitorMembers.Remove(row);
        foreach (var member in members)
        {
            var row = MonitorMembers.FirstOrDefault(r => r.Host == member.Host);
            if (row is null) MonitorMembers.Add(new(member)); else row.Update(member);
        }
    }
    public async Task ToggleAsync(Receiver receiver)
    {
        if (_closing) return;
        if (_snapshot.IsActive && _snapshot.Receiver?.Id == receiver.Id) { await StopAsync(); return; }
        var group = AllReceivers.FirstOrDefault(r => r.IsGroup && r.Members.Any(m => m.Address == receiver.Address));
        if (group is not null) receiver = group;
        await FlushVolumeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            if (_closing) return;
            _autoSuppressed = false; Notice = "";
            _autoAttempted.Add(receiver.Id);
            await Session.StartAsync(receiver, _settings.Clone());
            _settings.LastReceiverId = receiver.Id;
            VolumeChanged();
        }
        finally { _settingsGate.Release(); }
        await FlushVolumeAsync();
    }
    public async Task StopAsync(bool userInitiated = true)
    {
        if (userInitiated) _autoSuppressed = true;
        await Session.StopAsync();
    }
    private void ScheduleAutoConnect() { _autoTimer.Stop(); if (!_closing) _autoTimer.Start(); }
    private async Task TryAutoConnectAsync()
    {
        if (_closing || _autoSuppressed || Session.Snapshot.IsActive) return;
        var eligible = AllReceivers.Where(r => r.Online && r.Complete && !_settings.ReadOptions(r.Id).Hidden && (_settings.ReadOptions(r.Id).AutoConnect ?? _settings.AutoConnectOnDiscover));
        var receiver = eligible.OrderByDescending(r => r.Id == _settings.LastReceiverId).FirstOrDefault();
        if (receiver is not null && !_autoAttempted.Contains(receiver.Id)) await ToggleAsync(receiver);
    }
    public DeviceVolumeState ReceiverVolume(string id) => _snapshot.Receiver?.Id == id &&
        _snapshot.State is PlaybackState.Streaming or PlaybackState.Standby ? _snapshot.DeviceVolume ?? new() : new();
    public async Task ToggleReceiverMuteAsync(string id)
    {
        var volume = ReceiverVolume(id);
        if (!volume.Available) return;
        if (volume.Display == 0)
        {
            if (volume.LastNonZero is { } restore) await Session.SetDeviceVolumeAsync(id, restore);
        }
        else await Session.SetDeviceVolumeAsync(id, 0);
    }
    public Task SetReceiverVolumeAsync(string id, int value) => Session.SetDeviceVolumeAsync(id, value);
    private void VolumeChanged() { _editRevision++; _volumeDirty = true; _volumeTimer.Stop(); if (!_closing) _volumeTimer.Start(); }
    public async Task FlushVolumeAsync()
    {
        _volumeTimer.Stop();
        if (!_volumeDirty) return;
        await _settingsGate.WaitAsync();
        var saved = false;
        try
        {
            if (!_volumeDirty) return;
            var current = _settings.Clone();
            var revision = _editRevision;
            using (UiPerformance.Measure("settings.persist")) await _store.SaveAsync(current);
            saved = true;
            if (revision == _editRevision) _volumeDirty = false;
            await Session.UpdateSettingsAsync(current);
        }
        finally { _settingsGate.Release(); if (saved && _volumeDirty && !_closing) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public async Task<SettingsApplyResult> ApplyAsync(AppSettings baseline, AppSettings draft, Action<string>? progress = null)
    {
        if (_closing) throw new InvalidOperationException(L.Get("The application is closing."));
        baseline = baseline.Clone(); draft = draft.Clone();
        _volumeTimer.Stop();
        using (UiPerformance.Measure("apply.queue")) await _settingsGate.WaitAsync();
        try
        {
            if (_closing) throw new InvalidOperationException(L.Get("The application is closing."));
            _volumeTimer.Stop();
            var before = _settings.Clone();
            var revision = _editRevision;
            AppSettings merged;
            using (UiPerformance.Measure("apply.merge")) merged = SettingsMerge.Merge(baseline, draft, before);
            if (merged.Validate() is { } validation) throw new InvalidOperationException(validation);
            progress?.Invoke(L.Get("Saving settings…"));
            if (merged.CaptureMode == "endpoint")
            {
                using (UiPerformance.Measure("apply.endpoints"))
                    if (!(await Endpoints.RefreshAsync()).Any(e => e.Id == merged.CaptureEndpoint))
                        throw new InvalidOperationException(L.Get("The selected capture endpoint is unavailable. Select another."));
            }
            var changedAutostart = merged.StartAtLogin != before.StartAtLogin;
            using (UiPerformance.Measure("apply.persist"))
            {
                if (changedAutostart) await _autostart.SetAsync(merged.StartAtLogin);
                try { await _store.SaveAsync(merged); }
                catch (Exception saveError)
                {
                    if (changedAutostart)
                        try { await _autostart.SetAsync(before.StartAtLogin); }
                        catch (Exception rollbackError) { throw new AggregateException(L.Get("Saving failed and startup settings could not be restored."), saveError, rollbackError); }
                    throw;
                }
            }
            using (UiPerformance.Measure("apply.ui"))
            {
                if (!before.AutoConnectOnDiscover && merged.AutoConnectOnDiscover) { _autoSuppressed = false; _autoAttempted.Clear(); }
                // Preserve edits made in the panel while this snapshot was being saved.
                _settings = revision == _editRevision ? merged.Clone() : SettingsMerge.Merge(before, _settings, merged);
                foreach (var removed in baseline.Receivers.Keys.Except(draft.Receivers.Keys)) _settings.Receivers.Remove(removed);
                _volumeDirty = revision != _editRevision;
                var oldCatalog = AllReceivers;
                MergeManualReceivers(); RefreshReceivers();
                Notify(nameof(Settings)); Notify(nameof(MasterVolume)); Notify(nameof(LatencyMode));
                SettingsChanged?.Invoke();
                if (!CatalogEqual(oldCatalog, AllReceivers)) CatalogChanged?.Invoke();
            }
            progress?.Invoke(L.Get("Updating audio…"));
            string? audioError = null;
            try { using (UiPerformance.Measure("apply.session")) await Session.UpdateSettingsAsync(merged); }
            catch (Exception error) { audioError = L.Format("Settings saved, but audio could not be updated: {0}", error.Message); ShowError(new IOException(audioError, error)); }
            ScheduleAutoConnect();
            return new(merged, audioError);
        }
        finally { _settingsGate.Release(); if (_volumeDirty && !_closing) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public void ShowError(Exception error) { AppPaths.Log(error.ToString()); Notice = error.Message; }
    public string Diagnostics() => $"AirFlash {AppPaths.Version} / WPF\n" + L.Format("Engine: {0}\nStatus: {1}\n{2}\nLocal p95: {3}\nQueue age: {4}\nUnderruns: {5}; drops: {6}\nEnd-to-end latency: not measured", EngineVersion, StatusTitle, StatusDetail, MonitorLatency, MonitorQueue, MonitorUnderruns, MonitorDrops) + "\n" + System.Text.Json.JsonSerializer.Serialize(_snapshot.Diagnostics, AppSettings.JsonOptions);
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _closing = true; _volumeTimer.Stop(); _autoTimer.Stop(); _discovery.Dispose();
        await _startup;
        Endpoints.Dispose();
        try { await Endpoints.DrainAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        await _settingsGate.WaitAsync(); _settingsGate.Release();
        try { await FlushVolumeAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        await Session.DisposeAsync();
        if (_audio is IAsyncDisposable disposable) await disposable.DisposeAsync();
    }
}
public sealed record SettingsApplyResult(AppSettings Saved, string? AudioError)
{
    public bool AudioUpdated => AudioError is null;
}
public sealed class ReceiverViewModel : ObservableObject
{
    private readonly Dictionary<string, object?> _display = [];
    private readonly AppViewModel _app;
    public Receiver Receiver { get; set; }
    public ReceiverViewModel(AppViewModel app, Receiver receiver)
    {
        _app = app; Receiver = receiver;
        ToggleCommand = new(() => app.ToggleAsync(Receiver), app.ShowError, () => CanPlay);
        MuteCommand = new(() => app.ToggleReceiverMuteAsync(Receiver.Id), app.ShowError, () => CanMute);
    }
    public string Name => Receiver.Name;
    public string Detail => Receiver.Detail;
    private DeviceVolumeState DeviceVolume => _app.ReceiverVolume(Receiver.Id);
    public int Volume { get => DeviceVolume.Display ?? 0; set { if (Volume != value && CanSetVolume) _ = SetVolumeAsync(value); } }
    private async Task SetVolumeAsync(int value)
    {
        try { await _app.SetReceiverVolumeAsync(Receiver.Id, value); }
        catch (Exception error) { _app.ShowError(error); }
    }
    public string VolumeText => DeviceVolume.Display is { } value ? $"{value}%" : "—";
    public string VolumeStatus => DeviceVolume.Status switch {
        "pending" => L.Get("Synchronizing…"), "unconfirmed" => L.Get("Volume not confirmed"),
        "unsynced" => L.Get("Volume not synchronized"), _ => "" };
    public bool CanSetVolume => DeviceVolume.Available;
    public bool CanMute => CanSetVolume && (!Muted || DeviceVolume.LastNonZero is > 0);
    public bool Muted => DeviceVolume.Display == 0;
    public string MuteLabel => Muted ? L.Get("Unmute") : L.Get("Mute");
    public bool Active => _app.Snapshot.IsActive && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool CanPlay => Active || Receiver.Online && Receiver.Complete;
    public string PlayGlyph => Active ? "■" : "▶";
    public string PlayHint => Active ? L.Get("Stop playback / disconnect") : L.Get("Play on this device");
    public string StateText => (Active ? _app.StatusTitle : !Receiver.Online ? L.Get("Offline") : !Receiver.Complete ? L.Get("Waiting for the other member") : L.Get("Disconnected")) + (Receiver.IsGroup ? $" · {Receiver.Members.Length}/2" : "");
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand MuteCommand { get; }
    public void Refresh()
    {
        var values = new Dictionary<string, object?> { [nameof(Name)] = Name, [nameof(Detail)] = Detail, [nameof(Volume)] = Volume, [nameof(VolumeText)] = VolumeText, [nameof(VolumeStatus)] = VolumeStatus, [nameof(CanSetVolume)] = CanSetVolume, [nameof(CanMute)] = CanMute, [nameof(Muted)] = Muted, [nameof(MuteLabel)] = MuteLabel, [nameof(Active)] = Active, [nameof(CanPlay)] = CanPlay, [nameof(PlayGlyph)] = PlayGlyph, [nameof(PlayHint)] = PlayHint, [nameof(StateText)] = StateText };
        foreach (var (name, value) in values)
            if (!_display.TryGetValue(name, out var old) || !Equals(old, value))
            {
                _display[name] = value; Notify(name);
                if (name == nameof(CanPlay)) ToggleCommand.Refresh();
                if (name == nameof(CanMute)) MuteCommand.Refresh();
            }
    }
}

public sealed class TransportMemberViewModel(MemberTransportMetrics metrics) : ObservableObject
{
    public MemberTransportMetrics Metrics { get; private set; } = metrics;
    public void Update(MemberTransportMetrics value) { if (Metrics == value) return; Metrics = value; Notify(nameof(Summary)); }
    public string Host => Metrics.Host;
    private static string Count(long? value) => value?.ToString() ?? "—";
    public string Summary => string.Join("\n", new[]
    {
        L.Format("Packets sent: {0}; send errors: {1}; sync errors: {2}", Count(Metrics.PacketsSent), Count(Metrics.SendErrors), Count(Metrics.SyncErrors)),
        L.Format("Retransmission requests: {0}; packets resent: {1}", Count(Metrics.RetransmitRequests), Count(Metrics.RetransmitsSent)),
        L.Format("Not cached: {0}; expired: {1}; queue drops: {2}; resend errors: {3}", Count(Metrics.RetransmitMissing), Count(Metrics.RetransmitExpired), Count(Metrics.RetransmitQueueDrops), Count(Metrics.RetransmitSendErrors)),
        L.Format("Feedback: {0}; last successful response: {1}; failures: {2}", Metrics.FeedbackDelayed == true ? L.Get("Delayed") : Metrics.FeedbackRttMs is not null ? L.Get("Responding") : "—", Metrics.FeedbackRttMs is { } ms ? $"{ms:F1} ms" : "—", Count(Metrics.FeedbackFailures)),
        L.Format("Receiver delay parameter: {0} ({1})", Metrics.ReceiverLatencyMs is { } latency ? $"{latency:F1} ms" : "—", Metrics.ReceiverLatencyEstimated is { } estimated ? estimated ? L.Get("Requested value") : L.Get("Receiver-reported value") : "—")
    });
}
