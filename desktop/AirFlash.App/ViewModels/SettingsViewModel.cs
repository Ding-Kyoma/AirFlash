using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    public const int EqualizerPage = 2, AudioCapturePage = 3, ReceiversPage = 4, MonitorPage = 5, NetworkPage = 6;
    private readonly Guid _equalizerOwner = Guid.NewGuid();
    public EqualizerEditor Equalizer { get; }
    private string _equalizerError = "";
    public string EqualizerError { get => _equalizerError; private set => Set(ref _equalizerError, value); }
    private static readonly System.Net.Http.HttpClient UpdateClient = new() { MaxResponseContentBufferSize = 1024 * 1024 };
    public UpdateCheckState Updates { get; } = new(new UpdateService(UpdateClient), Version.Parse(AppPaths.Version));
    public AsyncCommand CheckUpdatesCommand { get; }
    public RelayCommand OpenDownloadCommand { get; }
    public AppViewModel App { get; }
    public AppSettings Draft { get; }
    private AppSettings _baseline;
    private readonly HashSet<ReceiverOptions> _subscribed = [];
    private CancellationTokenSource _profileStop = new();
    private readonly HashSet<string> _profileQueries = [];
    private readonly Dictionary<string, ReceiverCapabilitySet> _profileCache = [];
    private int _selectedPage, _validationErrors;
    private string _error = "", _progress = "";
    private string? _validation;
    private bool _applying, _hasChanges, _updating, _endpointLoading, _disposed;
    private TaskCompletionSource? _application;
    public bool IsApplying => _applying;
    public bool CanEdit => !_applying;
    public bool IsEndpointLoading => _endpointLoading;
    private bool _applicationsLoading;
    public bool IsApplicationsLoading => _applicationsLoading;
    public bool ApplicationCaptureSupported => App.Applications.Supported;
    public bool CanMuteEndpoint => Draft.CaptureMode != "application";
    public ObservableCollection<CaptureApplication> Applications { get; } = [];
    private CaptureApplication? _selectedApplication;
    public CaptureApplication? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (!Set(ref _selectedApplication, value) || _updating) return;
            Draft.CaptureApplicationPath = value?.Path;
            Draft.CaptureApplicationInstance = value is { ProcessId: > 0 } ? value : null;
            RefreshValidation();
        }
    }
    public AsyncCommand RefreshApplicationsCommand { get; }
    public RelayCommand OpenVolumeMixerCommand { get; }
    public Task ApplicationCompleted => _application?.Task ?? Task.CompletedTask;
    public string Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool HasChanges => _hasChanges;
    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!Set(ref _selectedPage, value)) return;
            Notify(nameof(PageTitle)); Notify(nameof(PageDescription));
            if (value == AudioCapturePage) { _ = LoadEndpointsAsync(false); if (Draft.CaptureMode == "application") _ = LoadApplicationsAsync(); }
            if (value == NetworkPage) RefreshAdapters();
            if (value == ReceiversPage) LoadReceiverProfiles();
        }
    }
    public string[] Pages { get; } = [L.Get("General"), L.Get("AirFlash streaming"), L.Get("Equalizer"), L.Get("Audio capture"), L.Get("Receivers"), L.Get("Monitor"), L.Get("Network"), L.Get("About")];
    public string PageTitle => Pages[Math.Clamp(SelectedPage, 0, Pages.Length - 1)];
    public string PageDescription => new[] { L.Get("Customize startup and appearance"), L.Get("Balance responsiveness and connection stability"), L.Get("Shape the sound sent to all receivers"), L.Get("Choose the audio source to send to HomePod"), L.Get("Manage receivers, connections and per-device settings"), L.Get("Live statistics for the current session"), L.Get("Choose where AirPlay receivers are discovered"), L.Get("Windows audio, wirelessly to HomePod") }[Math.Clamp(SelectedPage, 0, Pages.Length - 1)];
    public ObservableCollection<AudioEndpoint> Endpoints { get; } = [];
    public ObservableCollection<DiscoveryAdapterOption> DiscoveryAdapters { get; } = [];
    private string _networkStatus = "";
    public string NetworkStatus { get => _networkStatus; private set => Set(ref _networkStatus, value); }
    public RelayCommand RefreshAdaptersCommand { get; }
    public ObservableCollection<ReceiverEditor> Receivers { get; } = [];
    public ObservableCollection<SoftwareGroupEditor> Groups { get; } = [];
    public ObservableCollection<Receiver> GroupCandidates { get; } = [];
    public RelayCommand AddGroupCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand RefreshEndpointsCommand { get; }
    public AsyncCommand ResetCommand { get; }
    public Func<bool>? ConfirmReset { get; set; }
    public RelayCommand RefreshReceiverProfilesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public event Action<bool>? CloseRequested;
    public event Action? AddReceiverRequested;
    public event Action<Receiver>? PairRequested;
    public RelayCommand AddReceiverCommand { get; }
    public RelayCommand LicensesCommand { get; }
    public SettingsViewModel(AppViewModel app)
    {
        CheckUpdatesCommand = new(Updates.CheckAsync, _ => { });
        OpenDownloadCommand = new(() =>
        {
            try
            {
                if (Updates.DownloadPage is { } url)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) { ShowError(error); }
        });
        App = app; Draft = app.Settings.Clone(); _baseline = Draft.Clone();
        ResetCommand = new(ResetAsync, ShowError, () => CanEdit && !App.IsClosing);
        RefreshReceiverProfilesCommand = new(() => { _profileCache.Clear(); LoadReceiverProfiles(); }, () => CanEdit && App.EngineReady && !App.IsClosing);
        Equalizer = new(Draft.Equalizer, EqualizerSampleRate, EqualizerChanged);
        App.Session.EqualizerFeedback += EqualizerFeedback;
        ApplyCommand = new(async () => { await ApplyAsync(); }, ShowError, CanApply);
        OkCommand = new(async () => { if (!HasChanges || await ApplyAsync()) CloseRequested?.Invoke(true); }, ShowError, () => CanEdit && _validationErrors == 0 && _validation is null);
        CancelCommand = new(() => { if (CanEdit) CloseRequested?.Invoke(false); }, () => CanEdit);
        RefreshEndpointsCommand = new(() => LoadEndpointsAsync(true), ShowError, () => CanEdit && !_endpointLoading);
        RefreshApplicationsCommand = new(LoadApplicationsAsync, ShowError, () => CanEdit && !_applicationsLoading && ApplicationCaptureSupported);
        OpenVolumeMixerCommand = new(() =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true }); }
            catch (Exception error) { ShowError(error); }
        });
        RefreshAdaptersCommand = new(RefreshAdapters);
        OpenLogsCommand = new(() => { try { AppPaths.OpenLogs(); } catch (Exception error) { ShowError(error); } });
        CopyDiagnosticsCommand = new(() => { try { Clipboard.SetText(App.Diagnostics()); } catch (Exception error) { ShowError(error); } });
        AddReceiverCommand = new(() => { if (CanEdit) AddReceiverRequested?.Invoke(); }, () => CanEdit);
        AddGroupCommand = new(AddGroup, () => CanEdit && GroupCandidates.Count >= 2);
        LicensesCommand = new(() => new Ui.LicensesWindow().Show());
        Draft.PropertyChanged += DraftChanged;
        Draft.ManualReceivers.CollectionChanged += ManualChanged;
        Draft.SoftwareGroups.CollectionChanged += GroupsChanged;
        App.Session.Changed += VolumeChanged;
        RefreshCatalog(); RefreshValidation();
        App.CatalogChanged += RefreshCatalog;
        App.Endpoints.Changed += UpdateEndpoints;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        UpdateEndpoints(); RefreshAdapters();
    }
    private void SyncSubscriptions()
    {
        foreach (var options in _subscribed.Where(o => !Draft.Receivers.ContainsValue(o)).ToArray()) { options.PropertyChanged -= DraftChanged; _subscribed.Remove(options); }
        foreach (var options in Draft.Receivers.Values) if (_subscribed.Add(options)) options.PropertyChanged += DraftChanged;
    }
    private void ManualChanged(object? sender, NotifyCollectionChangedEventArgs args) { if (!_updating) RefreshValidation(); }
    private void VolumeChanged(SessionSnapshot snapshot) => Application.Current.Dispatcher.BeginInvoke(() => { if (!_disposed) { Equalizer.RefreshHeadroom(); foreach (var row in Receivers) { row.RefreshVolume(); row.RefreshProfiles(); } } });
    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs args) { if (!_updating) { RefreshGroups(); RefreshValidation(); } }
    private void AddGroup()
    {
        if (!CanEdit) return;
        var used = Draft.SoftwareGroups.SelectMany(g => new[] { g.LeftId, g.RightId }).ToHashSet();
        var candidates = GroupCandidates.Where(r => !used.Contains(r.Id)).Take(2).ToArray();
        if (candidates.Length != 2) { Error = L.Get("Stereo groups require two distinct independent receivers."); return; }
        Draft.SoftwareGroups.Add(new() { Name = L.Get("Software stereo"), LeftId = candidates[0].Id, RightId = candidates[1].Id, SampleRate = "44100" });
    }
    private void RefreshGroups()
    {
        foreach (var row in Groups.Where(r => !Draft.SoftwareGroups.Contains(r.Group)).ToArray()) { row.Dispose(); Groups.Remove(row); }
        foreach (var group in Draft.SoftwareGroups)
            if (Groups.All(r => r.Group != group)) Groups.Add(new(group, GroupCandidates, () => { if (CanEdit) { Draft.SoftwareGroups.Remove(group); Draft.Receivers.Remove(group.Id); } }, () => { RefreshGroupProfiles(); RefreshValidation(); }, () => CanEdit));
        RefreshGroupProfiles();
        AddGroupCommand.Refresh();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is ReceiverOptions) { Equalizer.RefreshHeadroom(); RefreshGroupProfiles(); }
        if (_updating) return;
        if (args.PropertyName == nameof(AppSettings.CaptureMode))
        {
            Notify(nameof(CanMuteEndpoint));
            if (Draft.CaptureMode == "application") _ = LoadApplicationsAsync();
        }
        RefreshValidation();
        if (args.PropertyName == nameof(AppSettings.DiscoveryInterfaceId)) RefreshAdapters();
    }
    private async void EqualizerChanged()
    {
        if (_updating || _disposed) return;
        RefreshValidation();
        if (Draft.Equalizer.Validate() is not null) return;
        EqualizerError = "";
        try { await App.Session.PreviewEqualizerAsync(_equalizerOwner, Draft.Equalizer); }
        catch (Exception error) { if (!_disposed) { EqualizerError = error.Message; AppPaths.Log(error.ToString()); } }
    }
    private int EqualizerSampleRate()
    {
        var snapshot = App.Session.Snapshot;
        return snapshot.IsActive && snapshot.StreamRate is 44100 or 48000 ? snapshot.StreamRate : 44100;
    }
    private void EqualizerFeedback(string? error)
    {
        if (_disposed) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            EqualizerError = error is null ? "" : L.Format("Could not update the equalizer: {0}", error);
            if (error is null) Equalizer.RefreshHeadroom();
        });
    }
    private async Task ClearPreviewAsync()
    {
        try { await App.Session.ClearEqualizerPreviewAsync(_equalizerOwner); }
        catch (Exception error) { if (!_disposed) EqualizerError = error.Message; AppPaths.Log(error.ToString()); }
    }
    private void RefreshValidation(bool clearError = true)
    {
        if (_updating || _disposed) return;
        _validation = Draft.Validate();
        _validation ??= Receivers.FirstOrDefault(r => r.HasInvalidProfile && ProfileEdited(r.Receiver.Id)) is { } invalid ? L.Format("Unsupported stream profile for {0}. Choose Automatic or a supported option.", invalid.Name) : null;
        _validation ??= Groups.FirstOrDefault(g => g.HasInvalidRate && (g.Group.SampleRate != _baseline.SoftwareGroups.FirstOrDefault(b => b.Id == g.Group.Id)?.SampleRate || g.Group.LeftId != _baseline.SoftwareGroups.FirstOrDefault(b => b.Id == g.Group.Id)?.LeftId || g.Group.RightId != _baseline.SoftwareGroups.FirstOrDefault(b => b.Id == g.Group.Id)?.RightId)) is { } group ? L.Format("Unsupported stream profile for {0}. Choose Automatic or a supported option.", group.Group.Name) : null;
        _hasChanges = !SettingsMerge.Equal(_baseline, Draft);
        if (clearError) Error = _validationErrors > 0 ? L.Get("Correct the highlighted fields before applying.") : _validation ?? "";
        Notify(nameof(HasChanges)); ApplyCommand.Refresh(); OkCommand.Refresh();
    }
    public void SetValidationErrors(int count) { _validationErrors = Math.Max(0, count); RefreshValidation(); }
    private bool CanApply() => CanEdit && _validationErrors == 0 && _validation is null && HasChanges;
    private bool ProfileEdited(string id)
    {
        var before = _baseline.ReadOptions(id); var after = Draft.ReadOptions(id);
        return before.Codec != after.Codec || before.Transport != after.Transport || before.Timing != after.Timing || before.SampleRate != after.SampleRate;
    }
    private void RefreshBusy()
    {
        Notify(nameof(IsApplying)); Notify(nameof(CanEdit));
        ApplyCommand.Refresh(); OkCommand.Refresh(); CancelCommand.Refresh(); AddReceiverCommand.Refresh(); RefreshEndpointsCommand.Refresh(); RefreshApplicationsCommand.Refresh();
        AddGroupCommand.Refresh();
        ResetCommand.Refresh(); RefreshReceiverProfilesCommand.Refresh();
        foreach (var group in Groups) { group.SwapCommand.Refresh(); group.RemoveCommand.Refresh(); }
        foreach (var row in Receivers) row.Refresh();
    }
    public async Task<bool> ApplyAsync()
    {
        if (_disposed || _applying) return false;
        RefreshValidation();
        if (_validationErrors > 0 || _validation is not null) return false;
        _applying = true; _application = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress = L.Get("Saving settings…"); RefreshBusy();
        try
        {
            var result = await App.ApplyAsync(_baseline, Draft, message => Progress = message);
            _updating = true;
            try
            {
                Draft.CopyFrom(result.Saved); _baseline = result.Saved.Clone();
                RefreshCatalog(); SyncSubscriptions(); UpdateEndpoints();
            }
            finally { _updating = false; }
            await ClearPreviewAsync();
            RefreshValidation();
            Error = result.AudioError ?? "";
            return result.AudioUpdated;
        }
        catch (Exception error) { ShowError(error); return false; }
        finally
        {
            _applying = false; Progress = ""; RefreshBusy();
            _application.TrySetResult();
        }
    }
    public async Task ResetAsync()
    {
        if (_disposed || _applying || App.IsClosing || ConfirmReset?.Invoke() != true) return;
        _applying = true; _application = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress = L.Get("Resetting settings and pairing…"); RefreshBusy();
        try
        {
            await _profileStop.CancelAsync();
            await App.ResetAsync();
        }
        catch (Exception error)
        {
            _profileStop.Dispose();
            _profileStop = new();
            _updating = true;
            try { Draft.CopyFrom(App.Settings); _baseline = Draft.Clone(); RefreshCatalog(); }
            finally { _updating = false; }
            ShowError(error); RefreshValidation(false);
        }
        finally { _applying = false; Progress = ""; RefreshBusy(); _application.TrySetResult(); }
    }
    private void RefreshCatalog()
    {
        if (_disposed) return;
        ReceiverCatalog.ApplyMigrations(Draft, App.ReceiverMigrations, App.Snapshot.Receiver?.Id);
        ReceiverCatalog.ApplyMigrations(_baseline, App.ReceiverMigrations, App.Snapshot.Receiver?.Id);
        Draft.ReceiverAliases = new(App.Settings.ReceiverAliases, StringComparer.Ordinal);
        _baseline.ReceiverAliases = new(App.Settings.ReceiverAliases, StringComparer.Ordinal);
        var manualIds = Draft.ManualReceivers.Select(r => r.Id).ToHashSet();
        var catalog = App.AllReceivers.Where(r => !r.IsSoftwareGroup && (!r.IsManual || manualIds.Contains(r.Id))).ToDictionary(r => r.Id);
        foreach (var manual in Draft.ManualReceivers) catalog[manual.Id] = new(manual.Id, manual.Name, manual.Host, manual.Port) { IsManual = true };
        var candidates = SoftwareStereo.Candidates(catalog.Values).OrderBy(r => r.Name).ToList();
        foreach (var id in Draft.SoftwareGroups.SelectMany(g => new[] { g.LeftId, g.RightId }))
            if (candidates.All(r => r.Id != id)) candidates.Add(Receiver.Offline(id));
        foreach (var old in GroupCandidates.Where(r => candidates.All(c => c.Id != r.Id)).ToArray()) GroupCandidates.Remove(old);
        foreach (var candidate in candidates) if (GroupCandidates.All(r => r.Id != candidate.Id)) GroupCandidates.Add(candidate);
        RefreshGroups();
        if (Draft.DiscoveryInterfaceId.Length == 0)
            foreach (var id in Draft.Receivers.Keys)
                if (!ReceiverCatalog.IsCoveredMember(id, App.AllReceivers)) catalog.TryAdd(id, Receiver.Offline(id));
        foreach (var row in Receivers.Where(r => !catalog.ContainsKey(r.Receiver.Id)).ToArray()) { row.Dispose(); Receivers.Remove(row); }
        var index = 0;
        foreach (var receiver in catalog.Values.OrderBy(r => r.Name))
        {
            var options = Draft.Options(receiver.Id);
            _baseline.Options(receiver.Id);
            var row = Receivers.FirstOrDefault(r => r.Receiver.Id == receiver.Id);
            if (row is null)
            {
                row = new(receiver, options, current => PairRequested?.Invoke(current), () => RemoveManual(receiver.Id), () => CanEdit, () => App.EngineReady && !App.IsClosing, App);
                row.GroupRate = () => Draft.SoftwareGroups.FirstOrDefault(g => g.LeftId == row.Receiver.Id || g.RightId == row.Receiver.Id)?.SampleRate;
                Receivers.Insert(index, row);
            }
            else
            {
                row.SetOptions(options);
                if (!AppViewModel.ReceiverEqual(row.Receiver, receiver)) { row.Receiver = receiver; row.Refresh(); }
                var oldIndex = Receivers.IndexOf(row); if (oldIndex != index) Receivers.Move(oldIndex, index);
                row.Refresh();
            }
            index++;
        }
        SyncSubscriptions();
        RefreshGroupProfiles();
        if (SelectedPage == ReceiversPage) LoadReceiverProfiles();
        RefreshReceiverProfilesCommand.Refresh();
        RefreshValidation(false);
    }
    private static string ProfileKey(Receiver receiver) => receiver.Id + "|" + receiver.TransportKey + "|" + string.Join(";", receiver.Peers.Select(p => $"{string.Join(',', p.Codecs)}:{p.Features}:{p.FollowReceiverClock}"));
    private void LoadReceiverProfiles()
    {
        if (_disposed || _profileStop.IsCancellationRequested || !App.EngineReady || App.IsClosing) return;
        foreach (var row in Receivers.Where(r => r.Receiver.Online && r.Receiver.Complete))
        {
            var key = ProfileKey(row.Receiver);
            if (_profileCache.TryGetValue(key, out var cached)) { row.SetCapabilities(cached); continue; }
            if (_profileQueries.Add(key)) _ = LoadReceiverProfileAsync(row, key, _profileStop.Token);
        }
    }
    private async Task LoadReceiverProfileAsync(ReceiverEditor row, string key, CancellationToken cancellation)
    {
        try
        {
            var result = await App.ReceiverCapabilities.QueryAsync(row.Receiver, cancellation);
            if (_disposed || cancellation.IsCancellationRequested || ProfileKey(row.Receiver) != key) return;
            _profileCache[key] = result; row.SetCapabilities(result); RefreshGroupProfiles(); RefreshValidation(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed && !cancellation.IsCancellationRequested) { row.CapabilityError = L.Format("Could not query receiver capabilities: {0}", error.Message); AppPaths.Log(error.ToString()); } }
        finally { _profileQueries.Remove(key); }
    }
    private void RefreshGroupProfiles()
    {
        foreach (var row in Receivers) row.RefreshProfiles();
        foreach (var group in Groups)
        {
            var left = Receivers.FirstOrDefault(r => r.Receiver.Id == group.Group.LeftId);
            var right = Receivers.FirstOrDefault(r => r.Receiver.Id == group.Group.RightId);
            var members = (left?.Capabilities.Members ?? []).Concat(right?.Capabilities.Members ?? []).ToArray();
            group.RefreshRates(left is not null && right is not null && members.Length == 2 ? new(members) : ReceiverCapabilitySet.Unknown, Draft);
        }
    }
    public void AddManual(string name, string host, int port)
    {
        if (!CanEdit) return;
        if (Draft.ManualReceivers.Any(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && r.Port == port)) { Error = L.Get("This address and port have already been added."); return; }
        var id = $"{host.ToLowerInvariant()}:{port}";
        Draft.ManualReceivers.Add(new(id, string.IsNullOrWhiteSpace(name) ? host : name, host, port));
        RefreshCatalog(); RefreshValidation();
    }
    private void RemoveManual(string id)
    {
        if (!CanEdit) return;
        var manual = Draft.ManualReceivers.FirstOrDefault(r => r.Id == id);
        if (manual is null) return;
        Draft.ManualReceivers.Remove(manual); Draft.Receivers.Remove(id);
        var row = Receivers.FirstOrDefault(r => r.Receiver.Id == id); if (row is not null) { row.Dispose(); Receivers.Remove(row); }
        SyncSubscriptions(); RefreshValidation();
    }
    public async Task LoadApplicationsAsync()
    {
        if (_disposed || _applicationsLoading || !ApplicationCaptureSupported) return;
        _applicationsLoading = true; Notify(nameof(IsApplicationsLoading)); RefreshApplicationsCommand.Refresh();
        var path = Draft.CaptureApplicationPath;
        var instance = Draft.CaptureApplicationInstance;
        try
        {
            var items = (await App.Applications.RefreshAsync()).ToList();
            if (_disposed) return;
            // Respect a user selection changed during this asynchronous refresh.
            path = Draft.CaptureApplicationPath; instance = Draft.CaptureApplicationInstance;
            var matches = items.Where(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var selected = matches.FirstOrDefault(p => p.ProcessId == instance?.ProcessId && p.StartedAt == instance.StartedAt)
                ?? (matches.Length == 1 ? matches[0] : null);
            if (!string.IsNullOrWhiteSpace(path) && selected is null)
            {
                selected = new(path, L.Get(matches.Length > 1 ? "Multiple instances · " : "Unavailable · ") + System.IO.Path.GetFileName(path), 0, 0);
                items.Add(selected);
            }
            _updating = true;
            try { Applications.Clear(); foreach (var item in items) Applications.Add(item); SelectedApplication = selected; }
            finally { _updating = false; }
            Notify(nameof(ApplicationCaptureSupported));
        }
        catch (Exception error) { if (!_disposed) ShowError(error); }
        finally { _applicationsLoading = false; Notify(nameof(IsApplicationsLoading)); RefreshApplicationsCommand.Refresh(); }
    }
    public async Task LoadEndpointsAsync(bool refresh)
    {
        if (_disposed || _endpointLoading) return;
        _endpointLoading = true; Notify(nameof(IsEndpointLoading)); RefreshEndpointsCommand.Refresh();
        try { if (refresh) await App.Endpoints.RefreshAsync(); else await App.Endpoints.GetAsync(); if (!_disposed) UpdateEndpoints(); }
        catch (Exception error) { if (!_disposed) ShowError(error); }
        finally { _endpointLoading = false; Notify(nameof(IsEndpointLoading)); RefreshEndpointsCommand.Refresh(); }
    }
    private void UpdateEndpoints()
    {
        if (_disposed) return;
        var selected = Draft.CaptureEndpoint;
        var items = (App.Endpoints.Items ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(selected) && items.All(e => e.Id != selected)) items.Add(new(selected, L.Get("Unavailable · ") + selected));
        if (Endpoints.SequenceEqual(items)) return;
        var updating = _updating; _updating = true;
        try
        {
            foreach (var item in Endpoints.Where(e => items.All(i => i.Id != e.Id)).ToArray()) Endpoints.Remove(item);
            for (var i = 0; i < items.Count; i++)
            {
                var old = Endpoints.FirstOrDefault(e => e.Id == items[i].Id);
                if (old is null) Endpoints.Insert(i, items[i]);
                else
                {
                    var index = Endpoints.IndexOf(old);
                    if (index != i) Endpoints.Move(index, i);
                    if (Endpoints[i] != items[i]) Endpoints[i] = items[i];
                }
            }
            Draft.CaptureEndpoint = selected;
            Draft.RefreshCaptureEndpoint();
        }
        finally { _updating = updating; }
    }
    private void NetworkChanged(object? sender, EventArgs args) => Application.Current.Dispatcher.BeginInvoke(RefreshAdapters);
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Application.Current.Dispatcher.BeginInvoke(RefreshAdapters);
    private void RefreshAdapters()
    {
        if (_disposed) return;
        try
        {
            var adapters = NetworkAdapterCatalog.List();
            var options = new List<DiscoveryAdapterOption> { new("", L.Get("All network interfaces (default)")) };
            options.AddRange(adapters.OrderByDescending(a => a.Available)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new DiscoveryAdapterOption(a.Id, a.Label)));
            var selected = Draft.DiscoveryInterfaceId;
            if (selected.Length > 0 && options.All(a => !a.Id.Equals(selected, StringComparison.OrdinalIgnoreCase)))
                options.Add(new(selected, L.Get("Unavailable · ") + selected));
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var current = DiscoveryAdapters.FirstOrDefault(a => a.Id == option.Id);
                if (current is null) DiscoveryAdapters.Insert(i, option);
                else
                {
                    if (current.Label != option.Label) current.Label = option.Label;
                    var previous = DiscoveryAdapters.IndexOf(current);
                    if (previous != i) DiscoveryAdapters.Move(previous, i);
                }
            }
            while (DiscoveryAdapters.Count > options.Count) DiscoveryAdapters.RemoveAt(DiscoveryAdapters.Count - 1);
            NetworkStatus = selected.Length == 0 ? L.Get("Discovery uses all network interfaces.") :
                DiscoveryInterface.ResolveIndex(selected, adapters) is null ? L.Get("The selected network interface is unavailable. Discovery is paused.") :
                L.Get("Discovery uses only the selected network interface.");
        }
        catch (NetworkInformationException error) { NetworkStatus = error.Message; }
    }
    private void ShowError(Exception error) { Error = error.Message; AppPaths.Log(error.ToString()); }
    public void Dispose()
    {
        if (_disposed) return;
        App.Session.EqualizerFeedback -= EqualizerFeedback; Equalizer.Dispose();
        _ = ClearPreviewAsync();
        Updates.Dispose(); _disposed = true; App.CatalogChanged -= RefreshCatalog; App.Endpoints.Changed -= UpdateEndpoints;
        _profileStop.Cancel();
        _profileStop.Dispose();
        NetworkChange.NetworkAddressChanged -= NetworkChanged; NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        App.SetMonitorVisible(false);
        Draft.PropertyChanged -= DraftChanged; Draft.ManualReceivers.CollectionChanged -= ManualChanged;
        Draft.SoftwareGroups.CollectionChanged -= GroupsChanged; App.Session.Changed -= VolumeChanged;
        foreach (var group in Groups) group.Dispose();
        foreach (var row in Receivers) row.Dispose();
        foreach (var options in _subscribed) options.PropertyChanged -= DraftChanged;
        _subscribed.Clear();
    }
}
public sealed class StreamChoice(object? value, string label) : ObservableObject
{
    public object? Value { get; } = value;
    private string _label = label;
    public string Label { get => _label; private set => Set(ref _label, value); }
    internal static void Update(ObservableCollection<StreamChoice> current, IEnumerable<Choice> choices)
    {
        var desired = choices.ToArray();
        foreach (var old in current.Where(c => desired.All(d => !Equals(c.Value, d.Value))).ToArray()) current.Remove(old);
        for (var i = 0; i < desired.Length; i++)
        {
            var item = current.FirstOrDefault(c => Equals(c.Value, desired[i].Value));
            if (item is null) current.Insert(i, new(desired[i].Value, desired[i].Label));
            else
            {
                item.Label = desired[i].Label;
                var previous = current.IndexOf(item);
                if (previous != i) current.Move(previous, i);
            }
        }
    }
}
public sealed class ReceiverEditor : ObservableObject, IDisposable
{
    private readonly AppViewModel? _app;
    private readonly Func<bool> _canEdit;
    private Receiver _receiver;
    public Receiver Receiver { get => _receiver; set { _receiver = value; Capabilities = ReceiverCapabilitySet.Unknown; RefreshProfiles(); } }
    public ReceiverOptions Options { get; private set; }
    public ReceiverCapabilitySet Capabilities { get; private set; } = ReceiverCapabilitySet.Unknown;
    public Func<string?>? GroupRate { get; set; }
    public bool CanSetSampleRate => GroupRate?.Invoke() is null;
    public string GroupRateHint => CanSetSampleRate ? "" : L.Get("The software stereo group controls this receiver's sample rate.");
    private string _capabilityError = "";
    public string CapabilityError { get => _capabilityError; set { if (Set(ref _capabilityError, value)) Notify(nameof(ProfileStatus)); } }
    private readonly ObservableCollection<StreamChoice> _codecs = [], _transports = [], _timings = [], _rates = [];
    public ObservableCollection<StreamChoice> CodecChoices => _codecs;
    public ObservableCollection<StreamChoice> TransportChoices => _transports;
    public ObservableCollection<StreamChoice> TimingChoices => _timings;
    public ObservableCollection<StreamChoice> RateChoices => _rates;
    public StreamChoice? CodecChoice { get => _codecs.FirstOrDefault(c => Equals(c.Value, Options.Codec)); set { if (value is not null && _canEdit()) Options.Codec = (string)value.Value!; } }
    public StreamChoice? TransportChoice { get => _transports.FirstOrDefault(c => Equals(c.Value, Options.Transport)); set { if (value is not null && _canEdit()) Options.Transport = (string)value.Value!; } }
    public StreamChoice? TimingChoice { get => _timings.FirstOrDefault(c => Equals(c.Value, Options.Timing)); set { if (value is not null && _canEdit()) Options.Timing = (string)value.Value!; } }
    public StreamChoice? RateChoice { get => _rates.FirstOrDefault(c => Equals(c.Value, EffectiveRate)); set { if (value is not null && _canEdit() && CanSetSampleRate) Options.SampleRate = (string?)value.Value; } }
    private string? EffectiveRate => GroupRate?.Invoke() ?? Options.SampleRate;
    public bool HasInvalidProfile => Capabilities.Known && Capabilities.Select(Options, EffectiveRate).Count == 0;
    public string ProfileStatus => string.Join("\n", new[] {
        CapabilityError,
        !Capabilities.Known ? L.Get("Receiver capabilities are not verified. Only Automatic is available.") : HasInvalidProfile ? L.Get("The saved profile is unsupported. Choose Automatic or a supported option.") : "",
        (!Capabilities.Known || HasInvalidProfile) && (Options.Codec != "auto" || Options.Transport != "auto" || Options.Timing != "auto" || Options.SampleRate is not null) ? L.Format("Saved profile: {0}", $"{Label(Options.Codec)} · {Label(Options.Transport)} · {Label(Options.Timing)} · {Label(EffectiveRate ?? "auto")}") : ""
    }.Where(s => s.Length > 0));
    public void SetOptions(ReceiverOptions options) { if (ReferenceEquals(Options, options)) return; Options.PropertyChanged -= Changed; Options = options; Options.PropertyChanged += Changed; Notify(nameof(Options)); RefreshProfiles(); }
    public void SetCapabilities(ReceiverCapabilitySet capabilities) { Capabilities = capabilities; CapabilityError = ""; RefreshProfiles(); }
    public ReceiverEditor(Receiver receiver, ReceiverOptions options, Action<Receiver> pair, Action remove, Func<bool> canEdit, Func<bool>? canPair = null, AppViewModel? app = null)
    {
        _app = app;
        _canEdit = canEdit; _receiver = receiver; Options = options;
        Options.PropertyChanged += Changed;
        PairCommand = new(() => { if (canEdit() && (canPair?.Invoke() ?? true)) pair(Receiver); }, () => canEdit() && (canPair?.Invoke() ?? true) && Receiver.Online && Receiver.Complete);
        RemoveCommand = new(() => { if (canEdit()) remove(); }, canEdit);
        RefreshProfiles();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => RefreshProfiles();
    private static string Label(string value) => value switch { "auto" => L.Get("Automatic"), "pcm" => "PCM", "alac" => "ALAC", "realtime" => L.Get("Real-time UDP"), "buffered" => L.Get("Buffered TCP"), "ptp" => L.Get("Sender PTP clock"), "ntp" => "NTP", "follow" => L.Get("Follow receiver PTP clock"), "44100" => "44.1 kHz", "48000" => "48 kHz", _ => value };
    public void RefreshProfiles()
    {
        Choice[] ChoicesFor(string field, IEnumerable<string> values)
        {
            var result = new List<Choice> { new("auto", AutoLabel(field)) };
            foreach (var value in values)
            {
                var candidate = new ReceiverOptions(); candidate.CopyFrom(Options);
                if (field == "codec") candidate.Codec = value;
                if (field == "transport") candidate.Transport = value;
                if (field == "timing") candidate.Timing = value;
                if (Capabilities.Known && Capabilities.Select(candidate, EffectiveRate).Count > 0) result.Add(new(value, Label(value)));
            }
            return result.ToArray();
        }
        StreamChoice.Update(_codecs, ChoicesFor("codec", ["pcm", "alac"]));
        StreamChoice.Update(_transports, ChoicesFor("transport", ["realtime", "buffered"]));
        StreamChoice.Update(_timings, ChoicesFor("timing", ["ptp", "ntp", "follow"]));
        StreamChoice.Update(_rates, CanSetSampleRate ? [new(null, AutoLabel("rate")), .. AppSettings.SampleRates.Where(r => Capabilities.Known && Capabilities.Select(Options, r).Count > 0).Select(r => new Choice(r, Label(r)))] : [new(EffectiveRate, Label(EffectiveRate!))]);
        foreach (var property in new[] { nameof(CodecChoice), nameof(TransportChoice), nameof(TimingChoice), nameof(RateChoice), nameof(CanSetSampleRate), nameof(GroupRateHint), nameof(ProfileStatus), nameof(HasInvalidProfile) }) Notify(property);

    }
    private string AutoLabel(string field)
    {
        var candidate = new ReceiverOptions(); candidate.CopyFrom(Options);
        if (field == "codec") candidate.Codec = "auto";
        if (field == "transport") candidate.Transport = "auto";
        if (field == "timing") candidate.Timing = "auto";
        var selections = Capabilities.Select(candidate, field == "rate" && CanSetSampleRate ? null : EffectiveRate);
        var active = _app?.Session.Snapshot;
        var applied = _app?.Settings.ReadOptions(Receiver.Id);
        var appliedGroup = _app?.Settings.SoftwareGroups.FirstOrDefault(g => g.LeftId == Receiver.Id || g.RightId == Receiver.Id)?.SampleRate;
        var isAutomatic = field switch { "codec" => Options.Codec == "auto", "transport" => Options.Transport == "auto", "timing" => Options.Timing == "auto", _ => EffectiveRate is null };
        var same = isAutomatic && applied is not null && applied.Codec == Options.Codec && applied.Transport == Options.Transport && applied.Timing == Options.Timing && (appliedGroup ?? applied.SampleRate) == EffectiveRate;
        var ids = Receiver.Peers.Select(p => p.Id).ToArray();
        var actual = same && active?.State == PlaybackState.Streaming && active.Receiver?.Peers.Any(p => ids.Contains(p.Id)) == true
            ? (active.Diagnostics?.Negotiated ?? []).Where(n => ids.Contains(n.Text("receiver_id"))).ToArray() : [];
        string summary;
        if (actual.Length == ids.Length)
        {
            summary = Join(actual.Select(n => (n.Text("receiver_id"), field switch {
                "codec" => Label(n.Text("codec")), "transport" => Label(n.Text("transport")), "rate" => Label(n.Integer("sample_rate")?.ToString() ?? ""),
                _ => ClockLabel(n, active!)
            })));
            return L.Format("Automatic ({0})", summary);
        }
        if (!Capabilities.Known || selections.Count == 0) return L.Get("Automatic (pending detection)");
        summary = Join(selections.Select(s => (s.ReceiverId, Label(field switch { "codec" => s.Profile.SelectedCodec, "transport" => s.Profile.SelectedTransport, "timing" => s.Profile.SelectedTiming, _ => s.Profile.SampleRate.ToString() }))));
        return L.Format("Automatic (expected: {0})", summary);
    }
    private static string ClockLabel(System.Text.Json.JsonElement negotiated, SessionSnapshot active)
    {
        if (negotiated.Text("timing") == "ntp") return "NTP";
        var label = Label(negotiated.Text("clock_role") == "follow" ? "follow" : "ptp");
        if (active.Diagnostics?.ClockStatus is { } status && status.TryGetProperty("members", out var members))
        {
            var clock = members.EnumerateArray().FirstOrDefault(m => m.Text("host") == negotiated.Text("host"));
            if (clock.ValueKind == System.Text.Json.JsonValueKind.Object) return Label(clock.Text("mode") == "receiver" ? "follow" : "ptp") + (clock.Boolean("ready") == true ? "" : " · " + L.Get("Synchronizing"));
        }
        return label;
    }
    private string Join(IEnumerable<(string Id, string Value)> values)
    {
        var array = values.ToArray();
        return array.Select(x => x.Value).Distinct().Count() == 1 ? array[0].Value : string.Join("; ", array.Select(x => $"{Receiver.Peers.FirstOrDefault(p => p.Id == x.Id)?.Name ?? x.Id}: {x.Value}"));
    }
    public string Name => Receiver.Name;
    public string Detail => $"{(Receiver.Address.Length > 0 ? ReceiverIdentity.Endpoint(Receiver.Address, Receiver.Port) : Receiver.Id)} · {(Receiver.Online ? Receiver.Detail : L.Get("Offline"))}";
    public bool IsManual => Receiver.IsManual;
    public bool CanSetVolume => _app?.ReceiverVolume(Receiver.Id).Available == true;
    public int Volume { get => _app?.ReceiverVolume(Receiver.Id).Display ?? 0; set { if (CanSetVolume && value != Volume) _ = WriteVolume(value); } }
    private async Task WriteVolume(int value) { try { await _app!.SetReceiverVolumeAsync(Receiver.Id, value); } catch (Exception error) { _app!.ShowError(error); } }
    public void RefreshVolume() { Notify(nameof(Volume)); Notify(nameof(CanSetVolume)); }
    public RelayCommand PairCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public void Refresh() { Notify(nameof(Name)); Notify(nameof(Detail)); Notify(nameof(IsManual)); PairCommand.Refresh(); RemoveCommand.Refresh(); RefreshProfiles(); }
    public void Dispose() => Options.PropertyChanged -= Changed;
}
public sealed class SoftwareGroupEditor : ObservableObject, IDisposable
{
    public SoftwareStereoGroup Group { get; }
    public ObservableCollection<Receiver> Candidates { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand RemoveCommand { get; }
    private readonly Action _changed;
    private readonly ObservableCollection<StreamChoice> _rates = [];
    public ObservableCollection<StreamChoice> RateChoices => _rates;
    public bool HasInvalidRate { get; private set; }
    public string RateStatus => HasInvalidRate ? L.Format("Unsupported group sample rate: {0}", Group.SampleRate == "44100" ? "44.1 kHz" : "48 kHz") : _rates.Count == 0 ? L.Format("Group sample rate: {0} (pending verification)", Group.SampleRate == "44100" ? "44.1 kHz" : "48 kHz") : "";
    public StreamChoice? RateChoice { get => _rates.FirstOrDefault(r => Equals(r.Value, Group.SampleRate)); set { if (value is not null) Group.SampleRate = (string)value.Value!; } }
    public void RefreshRates(ReceiverCapabilitySet capabilities, AppSettings settings)
    {
        StreamChoice.Update(_rates, AppSettings.SampleRates.Where(r => capabilities.Known && capabilities.Select(settings.ReadOptions, r).Count > 0).Select(r => new Choice(r, r == "44100" ? "44.1 kHz" : "48 kHz")));
        HasInvalidRate = capabilities.Known && capabilities.Select(settings.ReadOptions, Group.SampleRate).Count == 0;
        Notify(nameof(RateChoices)); Notify(nameof(RateChoice)); Notify(nameof(RateStatus));
    }
    public SoftwareGroupEditor(SoftwareStereoGroup group, ObservableCollection<Receiver> candidates, Action remove, Action changed, Func<bool> canEdit)
    { Group = group; Candidates = candidates; _changed = changed; SwapCommand = new(() => { if (canEdit()) group.Swap(); }, canEdit); RemoveCommand = new(remove, canEdit); Group.PropertyChanged += Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs args) => _changed();
    public void Dispose() => Group.PropertyChanged -= Changed;
}
