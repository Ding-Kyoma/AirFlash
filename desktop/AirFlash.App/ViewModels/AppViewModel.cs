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
    private readonly IMuteRecoveryService? _recovery;
    private string? _recoveryError;
    private readonly Dispatcher _dispatcher;
    private readonly EngineInitialization? _engine;
    private readonly IRestartService _restart;
    private static readonly System.Net.Http.HttpClient UpdateClient = new() { MaxResponseContentBufferSize = 1024 * 1024 };
    private bool _resetting, _resetCompleted;
    public IReceiverCapabilityCatalog ReceiverCapabilities { get; }
    public event Action? ResetCompleted;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly Dictionary<string, Receiver> _known = [];
    public IReadOnlyDictionary<string, string> ReceiverMigrations { get; private set; } = new Dictionary<string, string>();
    private readonly HashSet<string> _autoAttempted = [];
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _startupDiscoveryTimer;
    private readonly DispatcherTimer _discoverySearchTimer;
    private bool _started, _panelVisible, _receiversPageVisible, _startupDiscovery, _discoveryRequested, _isDiscovering, _searchSettled;
    public bool IsDiscovering => _isDiscovering;
    public string DiscoveryEmptyTitle => IsDiscovering && !_searchSettled ? L.Get("Searching for receivers") : L.Get("No receivers found");
    public string DiscoveryStatus => IsDiscovering ? L.Get("Searching for receivers") : L.Get("Discovery paused. Showing previous results.");
    private AppSettings _settings;
    private SessionSnapshot _snapshot = new(PlaybackState.Idle);
    private string _notice = "";
    private bool _noticeDismissed;
    private bool _volumeDirty, _volumeApplyPending, _autoSuppressed, _closing;
    private string? _defaultEndpoint;
    private long _editRevision;
    private Task _startup = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _monitorVisible;
    private readonly Dictionary<string, string> _monitorValues = [];
    public EndpointCatalog Endpoints { get; }
    public IApplicationCatalog Applications { get; }
    public UpdateCheckState Updates { get; }
    public bool IsClosing => _closing;
    public SessionController Session { get; }
    public NowPlayingPump NowPlaying { get; }
    private readonly CancellationTokenSource _nowPlayingStop;
    private readonly Task _nowPlayingWork;
    public ObservableCollection<ReceiverViewModel> Receivers { get; } = [];
    public IReadOnlyList<Receiver> AllReceivers => _known.Values.Concat(SoftwareStereo.Resolve(_known.Values, _settings)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public AppSettings Settings => _settings;
    public SessionSnapshot Snapshot => _snapshot;
    private Task? _stopTask;
    private long _automaticEligibilityRevision;
    private int _pendingEligibilityChanges;
    private bool _controlledScheduling;
    public bool IsStopping { get; private set; }
    private DiscoveryFailure? _discoveryNotice;
    public string Notice
    {
        get => _notice;
        private set => SetNotice(value);
    }
    private void SetNotice(string value, DiscoveryFailure? discoveryFailure = null)
    {
        _discoveryNotice = discoveryFailure;
        if (_notice == value) return;
        _notice = value;
        _noticeDismissed = false;
        Notify(nameof(Notice));
        RefreshNoticeBanner();
        Notify(nameof(ErrorDetails));
    }
    public bool HasNotice => !_noticeDismissed && !string.IsNullOrWhiteSpace(Notice);
    public RelayCommand DismissNoticeCommand { get; }
    private void DismissNotice()
    {
        if (!HasNotice) return;
        _noticeDismissed = true;
        RefreshNoticeBanner();
    }
    private void RefreshNoticeBanner()
    {
        Notify(nameof(HasNotice));
        DismissNoticeCommand.Refresh();
    }
    public int MasterVolume { get => _settings.MasterVolume; set { if (_closing || _resetting) return; value = Math.Clamp(value, 0, 100); if (value == _settings.MasterVolume) return; _settings.MasterVolume = value; VolumeChanged(); Notify(); } }
    public bool Muted => Session.Muted;
    public string MuteLabel => Muted ? L.Get("Unmute") : L.Get("Mute");
    public string StatusTitle => IsStopping ? L.Get("Disconnecting…") : Session.NeedsCleanup ? L.Get("Connection error") : _snapshot.State switch { PlaybackState.Connecting => L.Get("Connecting"), PlaybackState.Streaming => L.Get("Playing"), PlaybackState.Standby => L.Get("Standby"), PlaybackState.Pairing => L.Get("Pairing"), PlaybackState.Error => L.Get("Connection error"), PlaybackState.Preempted => L.Get("Taken over by another device"), _ => L.Get("Ready") };
    public string StatusDetail => IsStopping ? L.Get("Stopping playback and disconnecting…") : Session.NeedsCleanup ? L.Get("The audio engine could not stop. Try disconnecting again.") : _snapshot.Message.Length > 0 ? _snapshot.Message : _snapshot.IsActive
        ? L.Format("{0} · target {1} ms", _snapshot.Receiver?.Name, _snapshot.TargetLatency)
            + (_snapshot.PendingLatency is { } target ? " · " + L.Format("Adjusting to {0} ms…", target) : "")
        : L.Get("Select a receiver to play system audio");
    public string PanelStatusDetail => !IsStopping && !Session.NeedsCleanup && _snapshot.State == PlaybackState.Streaming
        && _snapshot.Message.Length == 0 && _snapshot.PendingLatency is null ? "" : StatusDetail;
    public string LatencyDetail
    {
        get
        {
            if (!_snapshot.IsActive || _snapshot.Receiver is not { } receiver) return "";
            var details = new List<string>();
            foreach (var item in _snapshot.Diagnostics?.Negotiated ?? [])
            {
                if (item.Number("effective_latency_ms") is not { } effective || item.Number("requested_latency_ms") is not { } requested) continue;
                var text = L.Format("Requested {0} ms · engine plan {1} ms", requested.ToString("0.#"), effective.ToString("0.#"));
                if (item.Number("receiver_latency_min_ms") is { } minimum)
                {
                    text += " · " + L.Format("Receiver reports a minimum of {0} ms.", minimum.ToString("0.#"));
                    if (item.Boolean("force_latency") == true && requested < minimum)
                        text += "\n" + L.Format("Below the receiver's reported minimum: continuing at {0} ms. Playback may be rejected or silent.", requested.ToString("0.#"));
                }
                if (receiver.IsGroup) text = (receiver.Peers.FirstOrDefault(p => p.Id == item.Text("receiver_id"))?.Name ?? receiver.Name) + ": " + text;
                details.Add(text);
            }
            return string.Join("\n", details);
        }
    }
    public string MonitorLatency => _snapshot.Metrics?.CaptureToSendP95 is { } ms ? $"{ms:F1} ms" : "—";
    public string MonitorQueue => _snapshot.Metrics?.MaxQueueAge is { } ms ? $"{ms:F1} ms" : "—";
    public string MonitorUnderruns => _snapshot.Metrics?.Underruns?.ToString() ?? "—";
    public string MonitorDrops => _snapshot.Metrics?.DroppedFrames?.ToString() ?? "—";
    public string MonitorPaddedFrames => _snapshot.Metrics?.PaddedFrames?.ToString() ?? "—";
    public string MonitorFullyPaddedPackets => _snapshot.Metrics?.FullyPaddedPackets?.ToString() ?? "—";
    public string MonitorRate => _snapshot.Metrics is { InputRate: > 0 } m ? $"{m.InputRate:N0} Hz" : "—";
    public string MonitorStreamRate => _snapshot.StreamRate is { } rate and > 0 ? $"{rate:N0} Hz · 16-bit" : "—";
    public string MonitorRecoveries => _snapshot.Diagnostics?.Transport?.SenderLateRecoveries?.ToString() ?? "—";
    public string MonitorSkipped => _snapshot.Diagnostics?.Transport?.SkippedPackets?.ToString() ?? "—";
    public string MonitorReconnects => _snapshot.Diagnostics?.ReconnectCount.ToString() ?? "—";
    public string MonitorSessionUptime => _snapshot.Diagnostics?.Transport?.SessionUptimeMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"d\.hh\:mm\:ss") : "—";
    public string MonitorWarnings => _snapshot.Diagnostics?.Warnings is { Count: > 0 } warnings ? string.Join("\n", warnings.Select(w => w.Detail)) : "—";
    public string MonitorLastFault => _snapshot.Diagnostics?.LastFault?.Detail ?? "—";
    public string MonitorNegotiated => string.Join("\n", (_snapshot.Diagnostics?.Negotiated ?? []).Select(n => $"{n.Text("host")} · {n.Text("codec")} · {n.Integer("sample_rate")} Hz · {n.Text("transport")} · {n.Text("timing")} · {n.Text("channel")} · {n.Integer("sync_offset_ms")} ms"))
        + "\n" + (_snapshot.Diagnostics?.ClockStatus?.ToString() ?? "—") + "\n" + (_snapshot.Diagnostics?.SchedulerStatus?.ToString() ?? "—");
    public ObservableCollection<TransportMemberViewModel> MonitorMembers { get; } = [];
    public string MonitorBeforeFault
    {
        get
        {
            if (_snapshot.Diagnostics is not { LastFault: not null } diagnostics) return "—";
            var capture = diagnostics.CaptureBeforeFault;
            var summary = L.Format("Local underruns: {0}; local drops: {1}; local p95: {2}", capture?.Underruns?.ToString() ?? "—", capture?.DroppedFrames?.ToString() ?? "—", capture?.CaptureToSendP95 is { } ms ? $"{ms:F1} ms" : "—");
            summary += "\n" + L.Format("Local padded output frames: {0}; wholly padded capture pulls: {1}",
                capture?.PaddedFrames?.ToString() ?? "—", capture?.FullyPaddedPackets?.ToString() ?? "—");
            if (diagnostics.TransportBeforeFault is { } transport)
            {
                summary += "\n" + L.Format("Sender recoveries: {0}; skipped packets: {1}", transport.SenderLateRecoveries?.ToString() ?? "—", transport.SkippedPackets?.ToString() ?? "—");
                summary += "\n" + string.Join("\n", transport.Members.Select(m => m.Host + "\n" + new TransportMemberViewModel(m).Summary));
            }
            return summary;
        }
    }
    public string ActionHint => _snapshot.State == PlaybackState.Error ? _snapshot.ActionHint : "";
    public string ErrorDetails => ErrorDetailsText.For(_snapshot, Notice);
    /// <summary>The receiver now belongs to another controller. Reconnecting is a
    /// deliberate user action, never the automatic transport retry.</summary>
    public bool CanReconnect => FindReconnectReceiver() is not null;
    public string EngineVersion { get; set; } = L.Get("Unknown");
    public bool EngineReady => _engine is null || _engine.State == EngineAvailability.Ready;
    public bool EngineFailed => _engine?.State == EngineAvailability.Failed;
    public bool MuteReady => !_settings.ShouldMuteEndpoint || _recovery is null || _recovery.Recovery.CanMute;
    public bool HasMuteRecovery => _recovery is not null && !_recovery.Recovery.CanMute;
    public MuteRecoverySnapshot MuteRecovery => _recovery?.Recovery ?? new(0, MuteRecoveryStatus.Ready, []);
    public string MuteRecoveryMessage => !HasMuteRecovery ? "" : _recoveryError ?? (_recovery!.Recovery.Status switch
    {
        MuteRecoveryStatus.NotInitialized => L.Get("Checking local audio recovery…"),
        MuteRecoveryStatus.Busy => L.Get("Another AirFlash instance owns local audio recovery. Muting playback is paused."),
        MuteRecoveryStatus.Unreadable => L.Get("Local audio recovery could not be read. Muting playback is paused."),
        MuteRecoveryStatus.Unrecognized => L.Get("The saved local audio recovery record is not supported. Review it before muting playback."),
        _ => L.Get("Review the previous local audio mute state before starting playback that mutes this PC.")
    });
    public AsyncCommand ReviewMuteRecoveryCommand { get; }
    public event Action? MuteRecoveryRequested;
    public string EngineMessage => EngineReady ? "" : EngineFailed
        ? L.Format("The audio engine could not start: {0}", _engine!.Error)
        : L.Get("Initializing audio engine…");
    public AsyncCommand RetryEngineCommand { get; }
    public bool CanEditGlobalLatency => !_closing && !_resetting && !IsStopping && !(_snapshot.State is PlaybackState.Connecting or PlaybackState.Streaming or PlaybackState.Standby && Session.UsesDeviceLatency(_settings.LatencyMode));
    public string GlobalLatencyHint => IsStopping ? L.Get("Disconnecting…") : CanEditGlobalLatency ? "" : L.Get("This device uses its receiver latency mode. Adjust it in Settings → Receivers.");
    public ObservableCollection<StreamChoice> LatencyChoices { get; } = [];
    public string LatencyMode { get => _settings.LatencyMode; set { if (!CanEditGlobalLatency || value == _settings.LatencyMode || value is null) return; _settings.LatencyMode = value; VolumeChanged(); Notify(); RefreshGlobalLatency(); } }
    private void RefreshGlobalLatency() { Notify(nameof(CanEditGlobalLatency)); Notify(nameof(GlobalLatencyHint)); }
    internal void RefreshLatencyChoices() => StreamChoice.Update(LatencyChoices, Choices.LatenciesFor(_settings.CustomBufferMs));
    public AsyncCommand StopCommand { get; }
    public AsyncCommand ReconnectCommand { get; }
    public AsyncCommand MuteCommand { get; }
    public event Action? SettingsChanged;
    public event Action? CatalogChanged;
    private sealed class EmptyNowPlayingSource : INowPlayingSource
    {
        public Task<NowPlaying?> ReadAsync(CancellationToken cancellation) => Task.FromResult<NowPlaying?>(null);
    }

    public AppViewModel(ISettingsStore store, IDiscoveryService discovery, IAutostart autostart, IAudioService audio, IEngineFactory factory, Dispatcher dispatcher, EngineInitialization? engine = null, IApplicationCatalog? applications = null, IReceiverCapabilityCatalog? receiverCapabilities = null, IRestartService? restart = null, bool isolatedVerification = false, UpdateCheckState? updates = null)
    {
        DismissNoticeCommand = new(DismissNotice, () => HasNotice);
        _engine = engine;
        _restart = restart ?? new RestartService();
        Updates = updates ?? new UpdateCheckState(new UpdateService(UpdateClient), Version.TryParse(AppPaths.Version, out var version) ? version : new Version(0, 0, 0));
        ReceiverCapabilities = receiverCapabilities ?? new ReceiverCapabilityCatalog(factory);
        RetryEngineCommand = new(() => _engine?.InitializeAsync() ?? Task.CompletedTask, ShowError, () => EngineFailed && !_closing);
        if (_engine is not null) _engine.Changed += EngineChanged;
        _store = store; _discovery = discovery; _autostart = autostart; _audio = audio; _dispatcher = dispatcher;
        Applications = applications ?? new ApplicationCatalog(factory);
        _recovery = audio as IMuteRecoveryService;
        if (_recovery is not null) _recovery.RecoveryChanged += RecoveryChanged;
        ReviewMuteRecoveryCommand = new(async () =>
        {
            await RefreshMuteRecoveryAsync();
            if (!_closing && HasMuteRecovery) MuteRecoveryRequested?.Invoke();
        }, RecoveryFailed, () => !_closing && HasMuteRecovery);
        Endpoints = new(audio, dispatcher);
        Endpoints.DevicesChanged += OnEndpointsChangedAsync;
        Endpoints.Failed += ShowError;
        _settings = store.Load();
        _discovery.SetInterface(_settings.DiscoveryInterfaceId);
        Session = new(factory, audio, AppPaths.Log, sessionRecords: isolatedVerification ? _ => { } : null);
        Session.Changed += SessionChanged;
        Session.EndpointRecoveryFailed += error => _dispatcher.BeginInvoke(() => ShowError(error));
        // A receiver's own buttons (or a remote pointed at it) arrive as commands for
        // the sender: transport commands go to SMTC or the media keys so the app that
        // is playing reacts, and mute/volume steps go to AirFlash's own controls on
        // the dispatcher. See RemoteCommand for what is deliberately ignored.
        _nowPlayingStop = new();
        if (isolatedVerification)
        {
            NowPlaying = new NowPlayingPump(new EmptyNowPlayingSource());
            _nowPlayingWork = Task.CompletedTask;
        }
        else
        {
            var nowPlayingSource = new AirFlash.App.Metadata.SmtcNowPlayingSource(AppPaths.Log);
            Session.OwnedRemoteCommandReceived += (command, authority) =>
            {
                switch (command.Target)
                {
                    case RemoteCommandTarget.MediaSession:
                        _ = nowPlayingSource.ExecuteAsync(command, () => CanApplyRemoteCommand(authority), _nowPlayingStop.Token);
                        break;
                    case RemoteCommandTarget.MediaKey when command.VirtualKey is { } key:
                        _dispatcher.BeginInvoke(() =>
                        {
                            if (CanApplyRemoteCommand(authority))
                                AirFlash.App.Input.MediaKeys.Press(key, AppPaths.Log);
                        });
                        break;
                    case RemoteCommandTarget.Mute:
                    case RemoteCommandTarget.MasterVolume:
                        _ = QueueRemoteAudioCommandAsync(command, authority);
                        break;
                }
            };
            // Now Playing is polled from SMTC and pushed only when it actually changed;
            // the engine turns it into the receiver's SET_PARAMETER payloads.
            NowPlaying = new NowPlayingPump(nowPlayingSource, log: AppPaths.Log);
            NowPlaying.SourceCleared += () => _ = Session.ClearNowPlayingAsync(_nowPlayingStop.Token);
            _nowPlayingWork = Task.Run(() => NowPlaying.RunAsync(now => _ = Session.PublishNowPlayingAsync(now, _nowPlayingStop.Token), _nowPlayingStop.Token));
        }
        _discovery.Changed += list =>
        {
            BeginEligibilityChange();
            _dispatcher.BeginInvoke(async () =>
            {
                try { await ApplyDiscoveryCoreAsync(list); }
                catch (Exception error) { ShowError(error); }
                finally { EndEligibilityChange(); }
            });
        };
        // WindowsDiscovery logs failures before publishing them. Present the
        // notice without repeating the synchronous file append on the dispatcher.
        _discovery.Failed += DiscoveryFailed;
        _discovery.ResolveSucceeded += resolution => _dispatcher.BeginInvoke(() =>
        {
            if (!_closing && _discoveryNotice is { Attempt: { } failure } && failure.Epoch <= resolution.Epoch
                && failure.Sequence < resolution.Sequence && failure.SameService(resolution)) Notice = "";
        });
        _discovery.RunningChanged += running => _dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            _isDiscovering = running && _discoveryRequested; Notify(nameof(IsDiscovering)); Notify(nameof(DiscoveryEmptyTitle)); Notify(nameof(DiscoveryStatus));
        });
        _volumeTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, async (_, _) => { _volumeTimer!.Stop(); try { await FlushVolumeAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _volumeTimer.Stop();
        _autoTimer = new(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) => { _autoTimer!.Stop(); try { await TryAutoConnectAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _autoTimer.Stop();
        _startupDiscoveryTimer = new(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            _startupDiscoveryTimer!.Stop(); _startupDiscovery = false; UpdateDiscoveryDemand();
        }, dispatcher);
        _startupDiscoveryTimer.Stop();
        _discoverySearchTimer = new(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            _discoverySearchTimer!.Stop(); _searchSettled = true; Notify(nameof(DiscoveryEmptyTitle));
        }, dispatcher);
        _discoverySearchTimer.Stop();
        StopCommand = new(() => StopAsync(), ShowError, () => !IsStopping && (_snapshot.IsActive || Session.NeedsCleanup));
        ReconnectCommand = new(() => FindReconnectReceiver() is { } receiver ? ToggleAsync(receiver) : Task.CompletedTask, ShowError, () => CanReconnect);
        MuteCommand = new(ToggleMuteAsync, ShowError);
        MergeManualReceivers();
        RefreshLatencyChoices();
    }
    public void Start()
    {
        _startup = StartAsync();
    }
    private bool CanApplyRemoteCommand(RemoteCommandAuthority authority)
        => !_closing && !_resetting && Session.IsRemoteCommandCurrent(authority);

    internal Task QueueRemoteAudioCommandAsync(RemoteCommand command, RemoteCommandAuthority authority)
    {
        return _dispatcher.InvokeAsync(ApplyAsync).Task.Unwrap();

        async Task ApplyAsync()
        {
            if (!CanApplyRemoteCommand(authority)) return;
            try
            {
                if (command.Target == RemoteCommandTarget.MasterVolume)
                    MasterVolume += command.VolumeDelta;
                else if (command.Target == RemoteCommandTarget.Mute && await Session.TryToggleRemoteMuteAsync(authority))
                    NotifyMuteChanged();
            }
            catch (Exception error) { ShowError(error); }
        }
    }

    private async Task ToggleMuteAsync()
    {
        await Session.SetMutedAsync(!Session.Muted);
        NotifyMuteChanged();
    }

    private void NotifyMuteChanged() { Notify(nameof(Muted)); Notify(nameof(MuteLabel)); }
    public Task Startup => _startup;
    internal async Task CheckUpdatesAtStartupAsync()
    {
        if (_closing || !_settings.CheckUpdatesOnStartup) return;
        try { await Updates.CheckAsync(); }
        catch (Exception error) { AppPaths.Log("Startup update check failed: " + error); }
    }
    private async Task StartAsync()
    {
        // Recovery does not display UI and runs before settings/lifecycle locks.
        if (_recovery is not null)
        {
            try { await _recovery.InitializeRecoveryAsync(); }
            catch (Exception error) { RecoveryFailed(error); }
            RefreshRecoveryDisplay();
        }
        if (_closing) return;
        await _settingsGate.WaitAsync();
        try
        {
            if (_store is SettingsStore { LoadWarning: { } warning }) Notice = warning;
            try { _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync(); }
            catch (Exception error) { ShowError(error); }
            if (_closing) return;
            try { await _autostart.SetAsync(_settings.StartAtLogin); }
            catch (Exception error) { ShowError(error); }
            await MigrateEndpointNameAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
        if (_closing) return;
        _started = true;
        _startupDiscovery = _settings.AutoConnectOnDiscover || _settings.Receivers.Values.Any(o => o.AutoConnect == true && !o.Hidden);
        if (_startupDiscovery) _startupDiscoveryTimer.Start();
        UpdateDiscoveryDemand();
        RefreshReceivers(); ScheduleAutoConnect();
    }
    public void SetPanelVisible(bool visible) { _panelVisible = visible; UpdateDiscoveryDemand(); }
    public void SetReceiversPageVisible(bool visible) { _receiversPageVisible = visible; UpdateDiscoveryDemand(); }
    private void DiscoveryFailed(DiscoveryFailure failure)
    {
        var context = failure.Reason is DiscoveryFailureReason.ResolveTimeout or DiscoveryFailureReason.ResolveFailed ? failure.Resolve : null;
        var attempt = context is not null ? Session.CaptureStreamingAttempt()?.Attempt : null;
        _dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (context is not null && attempt is { } original && Session.CaptureStreamingAttempt() is { } current
                && current.Attempt == original && DiscoveryFailureAttribution.IsOtherReceiver(context, current.Receiver, _known.Values, _settings.ReceiverAliases)) return;
            SetNotice(failure.Message, failure.Reason is DiscoveryFailureReason.ResolveTimeout or DiscoveryFailureReason.ResolveFailed ? failure : null);
        });
    }
    private void RefreshDiscoveryNotice()
    {
        if (_discoveryNotice?.Resolve is { } context && Session.CaptureStreamingAttempt() is { } current
            && DiscoveryFailureAttribution.IsOtherReceiver(context, current.Receiver, _known.Values, _settings.ReceiverAliases)) Notice = "";
    }
    private void UpdateDiscoveryDemand()
    {
        if (!_started || _closing || _resetting) return;
        var demand = (_panelVisible ? DiscoveryDemand.Panel : DiscoveryDemand.None)
            | (_receiversPageVisible ? DiscoveryDemand.Receivers : DiscoveryDemand.None)
            | (_startupDiscovery ? DiscoveryDemand.Startup : DiscoveryDemand.None)
            | (_snapshot.IsActive && _snapshot.Receiver?.IsSoftwareGroup == true ? DiscoveryDemand.SoftwareStereo : DiscoveryDemand.None);
        if (_discovery is IDiscoveryDiagnostics diagnostics) diagnostics.RecordDemand(demand);
        var requested = demand != DiscoveryDemand.None;
        if (requested == _discoveryRequested) return;
        _discoveryRequested = requested;
        if (requested)
        {
            _searchSettled = false; _discoverySearchTimer.Stop(); _discoverySearchTimer.Start(); try { _discovery.Start(); } catch (Exception error) { ShowError(error); }
        }
        else { _discoverySearchTimer.Stop(); try { _discovery.Stop(); } catch (Exception error) { ShowError(error); } }
        Notify(nameof(DiscoveryEmptyTitle));
    }
    private void EngineChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        if (EngineReady) EngineVersion = _engine!.Version;
        Notify(nameof(EngineReady)); Notify(nameof(EngineFailed)); Notify(nameof(EngineMessage));
        RetryEngineCommand.Refresh();
        RefreshReconnect();
        foreach (var row in Receivers) row.Refresh();
        CatalogChanged?.Invoke();
        if (EngineReady) ScheduleAutoConnect();
    });
    private void RecoveryChanged() => _dispatcher.BeginInvoke(() => { if (!_closing) RefreshRecoveryDisplay(); });
    private void RefreshRecoveryDisplay()
    {
        Notify(nameof(MuteReady)); Notify(nameof(HasMuteRecovery)); Notify(nameof(MuteRecovery)); Notify(nameof(MuteRecoveryMessage));
        ReviewMuteRecoveryCommand.Refresh();
        RefreshReconnect();
        foreach (var row in Receivers) row.Refresh();
        CatalogChanged?.Invoke();
        if (MuteReady && !_closing) ScheduleAutoConnect();
    }
    private void RecoveryFailed(Exception error)
    {
        AppPaths.Log(error.ToString()); _recoveryError = error.Message; RefreshRecoveryDisplay();
    }
    public async Task RefreshMuteRecoveryAsync()
    {
        if (_closing || _recovery is null) return;
        await _recovery.RefreshRecoveryAsync(); _recoveryError = null; RefreshRecoveryDisplay();
    }
    public async Task ResolveMuteRecoveryAsync(long revision, MuteRecoveryEntry entry, MuteRecoveryChoice choice)
    {
        if (_closing || _recovery is null) return;
        await _recovery.ResolveRecoveryAsync(revision, entry.Owner, entry.Generation, entry.EndpointId, choice);
        _recoveryError = null; RefreshRecoveryDisplay();
    }
    public async Task ArchiveMuteRecoveryAsync(long revision)
    {
        if (_closing || _recovery is null) return;
        await _recovery.ArchiveUnrecognizedRecoveryAsync(revision); _recoveryError = null; RefreshRecoveryDisplay();
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
    internal async Task OnEndpointsChangedAsync()
    {
        if (_closing || _resetting) return;
        var authority = Session.Capture();
        await _settingsGate.WaitAsync();
        try
        {
            if (_closing || _resetting) return;
            var next = await _audio.GetDefaultEndpointIdAsync();
            if (_closing || _resetting) return;
            var restarted = false;
            if (!IsStopping && _settings.CaptureMode == "loopback" && next != _defaultEndpoint && authority.Snapshot.IsActive)
                restarted = await Session.TryRestartForEndpointAsync(_settings.Clone(), authority.Owner, authority.AutomaticIntent, CaptureSessionSettingsContext());
            else if (!IsStopping && !authority.Snapshot.IsActive)
                await Session.TryRestoreAudioIfInactiveAsync(authority.Owner, authority.AutomaticIntent);
            var current = Session.Capture();
            // Pairing has not opened loopback capture yet. Once another owner is
            // playing, its next observation must still see an unhandled change.
            if (restarted || !current.Snapshot.IsActive || current.Snapshot.State == PlaybackState.Pairing
                || (current.Owner == authority.Owner && current.AutomaticIntent == authority.AutomaticIntent))
                _defaultEndpoint = next;
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
        try { await RefreshMuteRecoveryAsync(); } catch (Exception error) { RecoveryFailed(error); }
    }
    private void BeginEligibilityChange()
    {
        Interlocked.Increment(ref _pendingEligibilityChanges);
        Interlocked.Increment(ref _automaticEligibilityRevision);
    }

    private void EndEligibilityChange()
    {
        if (Interlocked.Decrement(ref _pendingEligibilityChanges) == 0) ScheduleAutoConnect();
    }

    internal async Task ApplyDiscoveryAsync(IReadOnlyList<Receiver> list)
    {
        BeginEligibilityChange();
        try { await ApplyDiscoveryCoreAsync(list); }
        finally { EndEligibilityChange(); }
    }

    private async Task ApplyDiscoveryCoreAsync(IReadOnlyList<Receiver> list)
    {
        if (_closing || _resetting) return;
        await _settingsGate.WaitAsync();
        try
        {
            if (!_closing && !_resetting) await ReconcileDiscoveryAsync(list);
        }
        finally { _settingsGate.Release(); if ((_volumeDirty || _volumeApplyPending) && !_closing && !_controlledScheduling) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    private async Task ReconcileDiscoveryAsync(IReadOnlyList<Receiver> list)
    {
        await MigrateMixedGroupsAsync(list);
        var currentSnapshot = Session.Snapshot;
        var discoverySession = currentSnapshot;
        var originalSettings = _settings.Clone();
        var reconciliation = ReceiverCatalog.Reconcile(list, _settings, currentSnapshot.Receiver?.Id);
        if (!SettingsMerge.Equal(_settings, reconciliation.Settings))
        {
            var revision = _editRevision;
            var volumeWasDirty = _volumeDirty;
            await SaveSettingsAsync(reconciliation.Settings, reconciliation.NeedsBackup);
            _volumeApplyPending |= volumeWasDirty;
            var live = _settings.Clone(); var baseline = originalSettings.Clone();
            ReceiverCatalog.ApplyMigrations(live, reconciliation.Migrations, currentSnapshot.Receiver?.Id);
            ReceiverCatalog.ApplyMigrations(baseline, reconciliation.Migrations, currentSnapshot.Receiver?.Id);
            _settings = SettingsMerge.Merge(baseline, live, reconciliation.Settings);
            _volumeDirty = revision != _editRevision;
            Notify(nameof(Settings)); SettingsChanged?.Invoke();
            var migrated = reconciliation.Migrations.Where(p => p.Key != p.Value && (originalSettings.Receivers.ContainsKey(p.Key) || currentSnapshot.Receiver?.Id == p.Key)).ToArray();
            if (migrated.Length > 0) AppPaths.Log("Receiver identity migrated: " + System.Text.Json.JsonSerializer.Serialize(migrated));
        }
        ReceiverMigrations = reconciliation.Migrations;
        list = reconciliation.Receivers;
        var before = AllReceivers;
        var discovered = list.ToDictionary(r => r.Id);
        foreach (var previous in before.Where(r => !r.IsManual))
            if (ReceiverMigrations.TryGetValue(previous.Id, out var target) && previous.Id != target && discovered.TryGetValue(target, out var promoted))
                AppPaths.Log("Receiver identity upgraded: " + System.Text.Json.JsonSerializer.Serialize(new { OldId = previous.Id, NewId = target, promoted.Address, promoted.Port, promoted.DeviceId, promoted.Model, promoted.StereoId,
                    Reason = ReceiverIdentity.IsBroadcast(previous.Id) ? "confirmed_broadcast_alias" : "unique_endpoint" }));
        foreach (var key in _known.Keys.ToArray())
            if (!_known[key].IsManual && (ReceiverMigrations.TryGetValue(key, out var target) && target != key || ReceiverCatalog.IsCoveredMember(key, list))) _known.Remove(key);
        foreach (var id in _autoAttempted.ToArray())
            if (ReceiverMigrations.TryGetValue(id, out var target) && id != target) { _autoAttempted.Remove(id); _autoAttempted.Add(target); }
        foreach (var key in _known.Keys.ToArray())
            if (!_known[key].IsManual && !discovered.ContainsKey(key))
            {
                if (_settings.DiscoveryInterfaceId.Length > 0) _known.Remove(key);
                else _known[key] = _known[key] with { Online = false };
            }
        foreach (var receiver in list)
        {
            var previous = before.Where(r => !r.IsManual && ReceiverMigrations.GetValueOrDefault(r.Id, r.Id) == receiver.Id)
                .OrderByDescending(r => r.Online).ThenByDescending(r => r.Complete).FirstOrDefault();
            if (previous is null || !previous.Online || !previous.Complete && receiver.Complete) _autoAttempted.Remove(receiver.Id);
            _known[receiver.Id] = receiver;
        }
        MergeManualReceivers();
        if (!CatalogEqual(before, AllReceivers)) { RefreshReceivers(); CatalogChanged?.Invoke(); }
        currentSnapshot = Session.Snapshot;
        if (currentSnapshot.Receiver is { IsManual: false } current)
        {
            var id = ReceiverMigrations.GetValueOrDefault(current.Id, current.Id);
            if (current.IsSoftwareGroup)
            {
                var resolved = SoftwareStereo.Resolve(_known.Values, _settings).FirstOrDefault(r => r.Id == id);
                if (resolved?.Members.Any(m => m.IsGroup || m.StereoId.Length > 0) == true)
                {
                    if (await Session.StopForDiscoveryConflictAsync(discoverySession))
                        ShowError(new IOException(L.Get("Stereo groups require two distinct independent receivers.")));
                    return;
                }
                if (resolved is { Online: true, Complete: true }) discovered[id] = resolved;
            }
            // Discovery snapshots can be incomplete while scanning or suspended.
            // The transport health checks own disconnect detection during playback.
            if (currentSnapshot.State != PlaybackState.Pairing && discovered.TryGetValue(id, out var next) && next.Complete)
                await Session.RefreshReceiverAsync(next, discoverySession, ReceiverMigrations);
        }
        ScheduleAutoConnect();
    }
    /// <summary>
    /// Acknowledge the current group invariant while preserving saved pairs.
    /// Incompatible pairs stay visible and can only be split by the user.
    /// </summary>
    private async Task MigrateMixedGroupsAsync(IReadOnlyList<Receiver> catalog)
    {
        if (_store is not SettingsStore store || !store.MixedGroupMigrationPending) return;
        var report = MixedGroupMigration.Apply(_settings, catalog, AppPaths.Log);
        if (MixedGroupMigration.IsSettled(_settings))
        {
            await store.CompleteMixedGroupMigrationAsync(_settings);
            Notify(nameof(Settings)); SettingsChanged?.Invoke();
        }
        else if (report.Changed)
        {
            await SaveSettingsAsync(_settings);
            Notify(nameof(Settings)); SettingsChanged?.Invoke();
        }
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
        => (left with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>(), Aliases = Array.Empty<string>() }) == (right with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>(), Aliases = Array.Empty<string>() })
            && left.Codecs.SequenceEqual(right.Codecs) && left.Aliases.SequenceEqual(right.Aliases) && CatalogEqual(left.Members, right.Members);
    private void RefreshReceivers()
    {
        var active = Session.Snapshot;
        var retainOwnedRow = active.IsActive || Session.NeedsCleanup;
        IEnumerable<Receiver> catalog = AllReceivers;
        if (retainOwnedRow && active.Receiver is { } current && catalog.All(r => r.Id != current.Id))
            catalog = catalog.Append(current with { Online = false });
        var visible = catalog.Where(r => retainOwnedRow && r.Id == active.Receiver?.Id
            || r.Online && !_settings.ReadOptions(r.Id).Hidden).ToList();
        foreach (var row in Receivers.Where(row => visible.All(r => r.Id != row.Receiver.Id)).ToArray()) Receivers.Remove(row);
        for (var i = 0; i < visible.Count; i++)
        {
            var row = Receivers.FirstOrDefault(r => r.Receiver.Id == visible[i].Id);
            if (row is null) { row = new(this, visible[i]); Receivers.Insert(i, row); }
            else { row.Receiver = visible[i]; var previous = Receivers.IndexOf(row); if (previous != i) Receivers.Move(previous, i); }
            row.Refresh();
        }
        Notify(nameof(AllReceivers));
        RefreshReconnect();
    }
    private void SessionChanged(SessionSnapshot snapshot) => _dispatcher.BeginInvoke(() => ApplySessionSnapshot(snapshot));
    private void ApplySessionSnapshot(SessionSnapshot snapshot)
    {
        // The panel stays open while Quit waits for an Apply or engine cleanup.
        if (!ReferenceEquals(snapshot, Session.Snapshot)) return;
        var title = StatusTitle; var detail = StatusDetail; var errors = ErrorDetails; var hint = ActionHint;
        var previous = _snapshot;
        _snapshot = snapshot;
        RememberSessionVolume(snapshot);
        if (Session.RequiresManualReconnect) _autoSuppressed = true;
        RefreshGlobalLatency();
        Notify(nameof(Snapshot)); Notify(nameof(LatencyDetail)); Notify(nameof(PanelStatusDetail));
        // Cleanup ownership may already have changed when the worker queued this snapshot.
        var needsCleanup = Session.NeedsCleanup;
        if (title != StatusTitle || needsCleanup) Notify(nameof(StatusTitle));
        if (detail != StatusDetail || needsCleanup) Notify(nameof(StatusDetail));
        if (errors != ErrorDetails) Notify(nameof(ErrorDetails));
        if (hint != ActionHint) Notify(nameof(ActionHint));
        if (previous.State != snapshot.State || previous.Receiver?.Id != snapshot.Receiver?.Id || needsCleanup)
        {
            RefreshDiscoveryNotice();
            RefreshReceivers();
            StopCommand.Refresh();
            UpdateDiscoveryDemand();
        }
        foreach (var row in Receivers) row.Refresh();
        if (_monitorVisible) RefreshMonitor();
    }
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
            [nameof(MonitorPaddedFrames)] = MonitorPaddedFrames, [nameof(MonitorFullyPaddedPackets)] = MonitorFullyPaddedPackets,
            [nameof(MonitorRate)] = MonitorRate, [nameof(MonitorStreamRate)] = MonitorStreamRate,
            [nameof(MonitorRecoveries)] = MonitorRecoveries,
            [nameof(MonitorSkipped)] = MonitorSkipped, [nameof(MonitorReconnects)] = MonitorReconnects,
            [nameof(MonitorSessionUptime)] = MonitorSessionUptime, [nameof(MonitorWarnings)] = MonitorWarnings,
            [nameof(MonitorLastFault)] = MonitorLastFault, [nameof(MonitorBeforeFault)] = MonitorBeforeFault
            , [nameof(MonitorNegotiated)] = MonitorNegotiated
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
        var intent = Session.BeginExplicitIntent();
        var current = Session.Capture().Snapshot;
        if (_closing || IsStopping) return;
        if (current.IsActive && current.Receiver?.Id == receiver.Id) { await StopAsync(); return; }
        if (!CanStartPlayback) return;
        receiver = ResolvePlayback(receiver);
        _autoSuppressed = false;
        await FlushVolumeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            if (!CanStartExplicit(intent)) return;
            if (_settings.CaptureMode == "loopback") _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync();
            if (!CanStartExplicit(intent)) return;
            receiver = FindCurrentPlaybackReceiver(receiver, AllReceivers)
                ?? throw new InvalidOperationException(L.Get("The receiver is unavailable."));
            if (_settings.ReadOptions(receiver.Id).Hidden) throw new InvalidOperationException(L.Get("The receiver is unavailable."));
            if (!await Session.TryStartExplicitAsync(receiver, _settings.Clone(), intent)) return;
            RecordAdmittedStart(receiver, intent);
        }
        finally { _settingsGate.Release(); }
        await FlushVolumeAsync();
    }
    private bool CanStartPlayback => !_closing && !_resetting && !IsStopping && EngineReady && MuteReady && !Session.NeedsCleanup;
    private bool CanStartExplicit(long intent) => CanStartPlayback && Session.IsIntentCurrent(intent);

    private Receiver? FindCurrentPlaybackReceiver(Receiver selected, IReadOnlyList<Receiver> catalog)
    {
        if (selected.IsManual)
        {
            // A queued Apply can remove or edit the selected manual endpoint.
            return _known.TryGetValue(selected.Id, out var manual) && manual.IsManual
                && StringComparer.OrdinalIgnoreCase.Equals(manual.Address, selected.Address) && manual.Port == selected.Port
                ? manual : null;
        }
        var id = ReceiverIdentity.Resolve(ReceiverMigrations.GetValueOrDefault(selected.Id, selected.Id), _settings.ReceiverAliases);
        return catalog.FirstOrDefault(item => item.Id == id);
    }

    private Receiver? FindReconnectReceiver()
    {
        if (!CanStartPlayback || _snapshot.IsActive || !Session.RequiresManualReconnect || _snapshot.Receiver is not { } receiver) return null;
        var catalog = AllReceivers;
        var selected = ReceiverSelection.ResolvePlayback(receiver, catalog);
        return selected is not null && FindCurrentPlaybackReceiver(selected, catalog) is { Online: true, Complete: true } current
            && !_settings.ReadOptions(current.Id).Hidden ? current : null;
    }

    private void RefreshReconnect()
    {
        Notify(nameof(CanReconnect));
        ReconnectCommand.Refresh();
    }

    private void RecordAdmittedStart(Receiver receiver, long intent)
    {
        if (Session.IsIntentCurrent(intent)) Notice = "";
        _autoAttempted.Add(receiver.Id);
        _settings.LastReceiverId = receiver.Id;
        VolumeChanged();
    }

    public Task StopAsync(bool userInitiated = true)
    {
        Session.InvalidateAutomaticIntent();
        if (userInitiated) _autoSuppressed = true;
        if (_stopTask is not null) return _stopTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTask = completion.Task;
        IsStopping = true; RefreshStopUi();
        _ = StopCoreAsync(completion);
        return completion.Task;
    }
    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await Session.StopAsync(); }
        catch (Exception error) { failure = error; }
        finally
        {
            ApplySessionSnapshot(Session.Snapshot);
            IsStopping = false; _stopTask = null; RefreshStopUi();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    private void RefreshStopUi()
    {
        Notify(nameof(IsStopping)); Notify(nameof(StatusTitle)); Notify(nameof(StatusDetail)); Notify(nameof(PanelStatusDetail));
        RefreshGlobalLatency(); StopCommand.Refresh(); RefreshReconnect();
        foreach (var row in Receivers) row.Refresh();
    }
    public async Task PairAsync(Receiver receiver, Func<Receiver, CancellationToken, Task<string?>> requestPin)
    {
        var intent = Session.BeginExplicitIntent();
        if (!CanStartExplicit(intent)) return;
        await Session.TryPairExplicitAsync(ResolvePlayback(receiver), _settings.Clone(), requestPin, intent);
    }
    private Receiver ResolvePlayback(Receiver receiver) => ReceiverSelection.ResolvePlayback(receiver, AllReceivers)
        ?? throw new InvalidOperationException(L.Get("The receiver selection is ambiguous."));

    internal void UseControlledScheduling()
    {
        _controlledScheduling = true;
        _volumeTimer.Stop();
        _autoTimer.Stop();
    }
    private void ScheduleAutoConnect() { _autoTimer.Stop(); if (!_closing && !_resetting && !_controlledScheduling) _autoTimer.Start(); }
    private async Task SaveSettingsAsync(AppSettings settings, bool? identityBackup = null)
    {
        var warning = (_store as SettingsStore)?.LoadWarning;
        if (identityBackup is { } backup) await _store.SaveReceiverIdentityAsync(settings, backup);
        else await _store.SaveAsync(settings);
        if (warning is not null && Notice == warning && _store is SettingsStore store && store.LoadWarning != warning)
            Notice = store.LoadWarning ?? "";
    }
    private bool CanAutoConnect => !_closing && !_resetting && !IsStopping && !Session.NeedsCleanup
        && !Session.RequiresManualReconnect && EngineReady && MuteReady && !_autoSuppressed && Volatile.Read(ref _pendingEligibilityChanges) == 0;

    internal async Task TryAutoConnectAsync()
    {
        var captured = Session.Capture();
        if (!CanAutoConnect || captured.Snapshot.IsActive) return;
        var selected = ReceiverSelection.SelectAutoConnect(AllReceivers, _settings, _autoAttempted);
        if (selected is null) return;
        await FlushVolumeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            if (!CanAutoConnect || !Session.IsIntentCurrent(captured.AutomaticIntent)) return;
            var revision = Volatile.Read(ref _automaticEligibilityRevision);
            var id = ReceiverIdentity.Resolve(ReceiverMigrations.GetValueOrDefault(selected.Id, selected.Id), _settings.ReceiverAliases);
            var receiver = AllReceivers.FirstOrDefault(item => item.Id == id);
            if (receiver is null || !ReceiverSelection.IsAutoConnectEligible(receiver, _settings, _autoAttempted)) return;
            if (_settings.CaptureMode == "loopback") _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync();
            if (!CanAutoConnect || !Session.IsIntentCurrent(captured.AutomaticIntent) || revision != Volatile.Read(ref _automaticEligibilityRevision))
            { ScheduleAutoConnect(); return; }
            if (!await Session.TryStartAutomaticAsync(receiver, _settings.Clone(), captured.Owner, captured.AutomaticIntent,
                () => revision == Volatile.Read(ref _automaticEligibilityRevision) && Volatile.Read(ref _pendingEligibilityChanges) == 0 && MuteReady))
            { ScheduleAutoConnect(); return; }
            RecordAdmittedStart(receiver, captured.AutomaticIntent);
        }
        finally { _settingsGate.Release(); }
        await FlushVolumeAsync();
    }
    public DeviceVolumeState ReceiverVolume(string id) => _snapshot.State is PlaybackState.Streaming or PlaybackState.Standby
        ? _snapshot.Receiver?.IsSoftwareGroup == true ? _snapshot.MemberVolumes?.GetValueOrDefault(id) ?? new() : _snapshot.Receiver?.Id == id ? _snapshot.DeviceVolume ?? new() : new() : new();
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
    public Task SetReceiverVolumeAsync(string id, int value)
    {
        RememberDeviceVolume(id, value);
        return Session.SetDeviceVolumeAsync(id, value);
    }
    /// <summary>
    /// Remember the last confirmed device volume so the next session can restore
    /// it before streaming starts. Zero is the mute state, not a volume, and
    /// never overwrites the last audible level.
    /// </summary>
    private void RememberSessionVolume(SessionSnapshot snapshot)
    {
        if (snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby)) return;
        if (snapshot.Receiver is { IsSoftwareGroup: true })
        {
            if (snapshot.MemberVolumes is not { } members) return;
            foreach (var (id, state) in members)
                if (state.Status == "confirmed") RememberDeviceVolume(id, state.Actual);
            return;
        }
        if (snapshot.Receiver is { } receiver && snapshot.DeviceVolume is { Status: "confirmed" } volume)
            RememberDeviceVolume(receiver.Id, volume.Actual);
    }
    private void RememberDeviceVolume(string id, int? value)
    {
        if (_closing || _resetting || value is not > 0) return;
        if (_snapshot.State is not (PlaybackState.Streaming or PlaybackState.Standby)) return;
        if (_snapshot.Receiver is not { } receiver) return;
        var active = receiver.IsSoftwareGroup ? _snapshot.MemberVolumes?.ContainsKey(id) == true : receiver.Id == id;
        if (!active || _settings.ReadOptions(id).LastVolume == value) return;
        _settings.Options(id).LastVolume = value;
        VolumeChanged();
    }
    private void VolumeChanged() { Interlocked.Increment(ref _automaticEligibilityRevision); _editRevision++; _volumeDirty = true; _volumeTimer.Stop(); if (!_closing && !_controlledScheduling) _volumeTimer.Start(); }
    private SessionSettingsContext CaptureSessionSettingsContext(IEnumerable<string>? clearedOptions = null, IEnumerable<string>? changedMemberships = null)
        => new(AllReceivers, ReceiverMigrations, clearedOptions, changedMemberships);

    public async Task FlushVolumeAsync()
    {
        _volumeTimer.Stop();
        if (_resetting || _resetCompleted || (!_volumeDirty && !_volumeApplyPending)) return;
        await _settingsGate.WaitAsync();
        var completed = false;
        try
        {
            if (_resetting || _resetCompleted || (!_volumeDirty && !_volumeApplyPending)) return;
            var current = _settings.Clone();
            var owner = Session.Capture();
            var context = CaptureSessionSettingsContext();
            var revision = _editRevision;
            if (_volumeDirty)
            {
                using (UiPerformance.Measure("settings.persist")) await SaveSettingsAsync(current);
                if (revision == _editRevision) _volumeDirty = false;
                _volumeApplyPending = true;
            }
            // Persistence and live application have different authorities. A newer
            // intent may reject this saved snapshot without admitting a new worker.
            // Retry from current settings and authority without repeating the save.
            if (!_closing) _volumeApplyPending = !await Session.TryUpdateSettingsAsync(current, owner, context);
            completed = true;
        }
        finally { _settingsGate.Release(); if (completed && (_volumeDirty || _volumeApplyPending) && !_closing && !_controlledScheduling) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public async Task<SettingsApplyResult> ApplyAsync(AppSettings baseline, AppSettings draft, Action<string>? progress = null)
    {
        if (_closing || _resetting) throw new InvalidOperationException(L.Get("The application is closing."));
        baseline = baseline.Clone(); draft = draft.Clone();
        _volumeTimer.Stop();
        BeginEligibilityChange();
        using (UiPerformance.Measure("apply.queue")) await _settingsGate.WaitAsync();
        try
        {
            if (_closing || _resetting) throw new InvalidOperationException(L.Get("The application is closing."));
            _volumeTimer.Stop();
            ReceiverCatalog.ApplyMigrations(baseline, ReceiverMigrations, Session.Snapshot.Receiver?.Id);
            ReceiverCatalog.ApplyMigrations(draft, ReceiverMigrations, Session.Snapshot.Receiver?.Id);
            var before = _settings.Clone();
            var owner = Session.Capture();
            var revision = _editRevision;
            var volumeWasDirty = _volumeDirty;
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
            if (merged.CaptureMode == "application" && !Applications.Supported) throw new InvalidOperationException(L.Get("Application capture requires Windows build 20348 or later. Use a virtual audio endpoint on this Windows version."));
            var changedAutostart = merged.StartAtLogin != before.StartAtLogin;
            using (UiPerformance.Measure("apply.persist"))
            {
                if (changedAutostart) await _autostart.SetAsync(merged.StartAtLogin);
                try { await SaveSettingsAsync(merged); }
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
                if (!before.AutoConnectOnDiscover && merged.AutoConnectOnDiscover)
                {
                    if (Session.IsIntentCurrent(owner.AutomaticIntent)) _autoSuppressed = false;
                    _autoAttempted.Clear();
                }
                // Preserve edits made in the panel while this snapshot was being saved.
                _settings = revision == _editRevision ? merged.Clone() : SettingsMerge.Merge(before, _settings, merged);
                foreach (var removed in baseline.Receivers.Keys.Except(draft.Receivers.Keys)) _settings.Receivers.Remove(removed);
                _volumeApplyPending |= volumeWasDirty;
                _volumeDirty = revision != _editRevision;
                if (before.DiscoveryInterfaceId != _settings.DiscoveryInterfaceId) _discovery.SetInterface(_settings.DiscoveryInterfaceId);
                var oldCatalog = AllReceivers;
                MergeManualReceivers(); RefreshReceivers();
                RefreshLatencyChoices();
                Notify(nameof(Settings)); Notify(nameof(MasterVolume)); Notify(nameof(LatencyMode));
                RefreshGlobalLatency();
                RefreshRecoveryDisplay();
                SettingsChanged?.Invoke();
                if (!CatalogEqual(oldCatalog, AllReceivers)) CatalogChanged?.Invoke();
            }
            progress?.Invoke(L.Get("Updating audio…"));
            string? audioError = null;
            try
            {
                using (UiPerformance.Measure("apply.session"))
                {
                    if (!_closing && Session.IsIntentCurrent(owner.AutomaticIntent))
                    {
                        var changedMemberships = draft.SoftwareGroups.Where(group => baseline.SoftwareGroups.FirstOrDefault(previous => previous.Id == group.Id) is { } previous
                            && (previous.IsMultiRoom != group.IsMultiRoom || !previous.MemberIds.SequenceEqual(group.MemberIds)))
                        .Select(group => group.Id).ToArray();
                        var context = CaptureSessionSettingsContext(baseline.Receivers.Keys.Except(draft.Receivers.Keys), changedMemberships);
                        var expected = owner;
                        if (owner.Snapshot.Receiver is { IsSoftwareGroup: true } active && changedMemberships.Contains(active.Id)
                            && AllReceivers.FirstOrDefault(receiver => receiver.Id == active.Id) is { Online: true, Complete: true } next)
                        {
                            if (await Session.TryUpdateReceiverAsync(next, merged, owner))
                                expected = Session.Capture();
                        }
                        if (expected.AutomaticIntent == owner.AutomaticIntent)
                            if (await Session.TryUpdateSettingsAsync(merged, expected, context)) _volumeApplyPending = false;
                    }
                }
            }
            catch (Exception error) { audioError = L.Format("Settings saved, but audio could not be updated: {0}", error.Message); ShowError(new IOException(audioError, error)); }
            ScheduleAutoConnect();
            return new(merged, audioError);
        }
        finally { _settingsGate.Release(); EndEligibilityChange(); if ((_volumeDirty || _volumeApplyPending) && !_closing && !_controlledScheduling) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public void ShowError(Exception error) { AppPaths.Log(error.ToString()); Notice = error.Message; }
    public async Task ResetAsync()
    {
        if (_closing || _resetting) return;
        Session.InvalidateAutomaticIntent();
        Interlocked.Increment(ref _automaticEligibilityRevision);
        _resetting = true;
        RefreshReconnect();
        var acquired = false; var cleared = false; var oldStartup = _settings.StartAtLogin;
        try
        {
            _volumeTimer.Stop(); _autoTimer.Stop(); _startupDiscoveryTimer.Stop(); _discoverySearchTimer.Stop();
            _autoSuppressed = true; _startupDiscovery = false; _discoveryRequested = false; _discovery.Stop();
            RefreshGlobalLatency();
            await _settingsGate.WaitAsync(); acquired = true;
            await Session.StopAsync();
            await _audio.RestoreAsync();
            await _autostart.SetAsync(false);
            await _store.ResetAsync(); cleared = true;
            _settings = new(); _volumeDirty = false; _volumeApplyPending = false; _editRevision++;
            ReceiverMigrations = new Dictionary<string, string>(); _autoAttempted.Clear();
            _discovery.SetInterface(_settings.DiscoveryInterfaceId);
            _known.Clear(); MergeManualReceivers(); RefreshReceivers();
            RefreshLatencyChoices();
            Notify(nameof(Settings)); Notify(nameof(MasterVolume)); Notify(nameof(LatencyMode));
            _restart.Schedule();
            _resetCompleted = true;
            _closing = true;
            SettingsChanged?.Invoke(); CatalogChanged?.Invoke();
        }
        catch (Exception error)
        {
            if (!cleared)
                try { await _autostart.SetAsync(oldStartup); }
                catch (Exception rollback) { throw new AggregateException(L.Get("Reset failed and startup settings could not be restored."), error, rollback); }
            throw;
        }
        finally
        {
            if (acquired) _settingsGate.Release(); _resetting = false; RefreshGlobalLatency(); RefreshReconnect();
            if (!_closing) UpdateDiscoveryDemand();
        }
        ResetCompleted?.Invoke();
    }
    public string Diagnostics() => $"AirFlash {AppPaths.DisplayVersion} / WPF\n" + L.Format("Engine: {0}\nStatus: {1}\n{2}\nLocal p95: {3}\nQueue age: {4}\nUnderruns: {5}; drops: {6}\nEnd-to-end latency: not measured", EngineVersion, StatusTitle, StatusDetail, MonitorLatency, MonitorQueue, MonitorUnderruns, MonitorDrops)
        + "\n" + L.Format("Local padded output frames: {0}; wholly padded capture pulls: {1}", MonitorPaddedFrames, MonitorFullyPaddedPackets)
        + (ActionHint.Length > 0 ? "\n" + ActionHint : "") + "\n" + System.Text.Json.JsonSerializer.Serialize(_snapshot.Diagnostics, AppSettings.JsonOptions);
    /// <summary>Write the current engine's redacted diagnostic bundle into the log folder.</summary>
    public async Task<string> ExportDiagnosticsAsync()
    {
        var capture = await DiagnosticExport.CaptureAsync(Session).ConfigureAwait(false);
        var discovery = (_discovery as IDiscoveryDiagnostics)?.SnapshotDiagnostics();
        var bundle = DiagnosticExport.Compose(AppPaths.DisplayVersion, EngineVersion, capture.Summary, capture.CounterHistory, discovery);
        var path = DiagnosticExport.Write(AppPaths.LogDirectory, bundle);
        AppPaths.Log($"Diagnostics exported to {path}");
        return path;
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    internal void BeginShutdown()
    {
        if (_closing) return;
        _closing = true;
        Session.InvalidateAutomaticIntent();
        Interlocked.Increment(ref _automaticEligibilityRevision);
        _volumeTimer.Stop(); _autoTimer.Stop(); _startupDiscoveryTimer.Stop(); _discoverySearchTimer.Stop();
        RefreshReconnect();
        foreach (var row in Receivers) row.Refresh();
    }
    private async Task DisposeCoreAsync()
    {
        BeginShutdown(); Updates.Dispose(); _discovery.Dispose();
        if (_recovery is not null) _recovery.RecoveryChanged -= RecoveryChanged;
        _nowPlayingStop.Cancel();
        await _nowPlayingWork;
        _nowPlayingStop.Dispose();
        if (_engine is not null) _engine.Changed -= EngineChanged;
        await _startup;
        Endpoints.Dispose();
        try { await Endpoints.DrainAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        await _settingsGate.WaitAsync(); _settingsGate.Release();
        try { await FlushVolumeAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        try { await Session.DisposeAsync(); }
        finally
        {
            if (_audio is IAsyncDisposable disposable)
                try { await disposable.DisposeAsync(); }
                catch (Exception error) { AppPaths.Log("Local audio cleanup failed: " + error); }
        }
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
    public ObservableCollection<ReceiverViewModel> MemberVolumes { get; } = [];
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
    // The volume row renders fixed-size icons instead of status text so translations cannot wrap or add a row.
    public bool VolumeSyncing => DeviceVolume.Status == "pending";
    public bool VolumeWarning => DeviceVolume.Status is "unconfirmed" or "unsynced";
    public bool VolumeAlert => VolumeSyncing || VolumeWarning;
    public bool CanSetVolume => DeviceVolume.Available;
    public bool HasSingleVolume => !Receiver.IsSoftwareGroup;
    public bool ShowVolumeControls => !IsStopping && _app.Snapshot.Receiver?.Id == Receiver.Id
        && _app.Snapshot.State is PlaybackState.Streaming or PlaybackState.Standby;
    public bool CanMute => CanSetVolume && (!Muted || DeviceVolume.LastNonZero is > 0);
    public bool Muted => DeviceVolume.Display == 0;
    public string MuteLabel => Muted ? L.Get("Unmute") : L.Get("Mute");
    public bool Active => _app.Snapshot.IsActive && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool IsStopping => _app.IsStopping && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool NeedsDisconnect => Active || _app.Session.NeedsCleanup && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public System.Windows.Input.ICommand PlaybackCommand => NeedsDisconnect ? _app.StopCommand : ToggleCommand;
    public bool CanPlay => !_app.IsStopping && !_app.Session.NeedsCleanup
        && (Active || !_app.IsClosing && _app.EngineReady && _app.MuteReady && Receiver.Online && Receiver.Complete);
    public string PlayHint => IsStopping ? L.Get("Disconnecting…") : NeedsDisconnect ? L.Get("Stop playback / disconnect") : L.Get("Play on this device");
    public string StateText => (NeedsDisconnect ? _app.StatusTitle : !Receiver.Online ? L.Get("Offline") : !Receiver.Complete ? L.Get(Receiver.IsMultiRoom ? "Waiting for other members" : "Waiting for the other member") : L.Get("Disconnected")) + (Receiver.IsGroup ? $" · {Receiver.GroupDisplayMemberCount}/{(Receiver.IsMultiRoom ? Receiver.Members.Length : 2)}" : "");
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand MuteCommand { get; }
    public void Refresh()
    {
        var members = Receiver.IsSoftwareGroup ? Receiver.Members : [];
        foreach (var row in MemberVolumes.Where(r => members.All(m => m.Id != r.Receiver.Id)).ToArray()) MemberVolumes.Remove(row);
        foreach (var member in members)
        { var row = MemberVolumes.FirstOrDefault(r => r.Receiver.Id == member.Id); if (row is null) MemberVolumes.Add(row = new(_app, member)); else row.Receiver = member; row.Refresh(); }
        var values = new Dictionary<string, object?> { [nameof(IsStopping)] = IsStopping, [nameof(NeedsDisconnect)] = NeedsDisconnect, [nameof(PlaybackCommand)] = PlaybackCommand, [nameof(HasSingleVolume)] = HasSingleVolume, [nameof(ShowVolumeControls)] = ShowVolumeControls, [nameof(Name)] = Name, [nameof(Detail)] = Detail, [nameof(Volume)] = Volume, [nameof(VolumeText)] = VolumeText, [nameof(VolumeStatus)] = VolumeStatus, [nameof(VolumeSyncing)] = VolumeSyncing,
            [nameof(VolumeWarning)] = VolumeWarning, [nameof(VolumeAlert)] = VolumeAlert, [nameof(CanSetVolume)] = CanSetVolume, [nameof(CanMute)] = CanMute, [nameof(Muted)] = Muted, [nameof(MuteLabel)] = MuteLabel, [nameof(Active)] = Active, [nameof(CanPlay)] = CanPlay, [nameof(PlayHint)] = PlayHint, [nameof(StateText)] = StateText };
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
