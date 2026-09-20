using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly System.Net.Http.HttpClient UpdateClient = new() { MaxResponseContentBufferSize = 1024 * 1024 };
    public UpdateCheckState Updates { get; } = new(new UpdateService(UpdateClient), Version.Parse(AppPaths.Version));
    public AsyncCommand CheckUpdatesCommand { get; }
    public RelayCommand OpenDownloadCommand { get; }
    public AppViewModel App { get; }
    public AppSettings Draft { get; }
    private AppSettings _baseline;
    private readonly HashSet<ReceiverOptions> _subscribed = [];
    private int _selectedPage, _validationErrors;
    private string _error = "", _progress = "";
    private string? _validation;
    private bool _applying, _hasChanges, _updating, _endpointLoading, _disposed;
    private TaskCompletionSource? _application;
    public bool IsApplying => _applying;
    public bool CanEdit => !_applying;
    public bool IsEndpointLoading => _endpointLoading;
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
            if (value == 2) _ = LoadEndpointsAsync(false);
        }
    }
    public string[] Pages { get; } = [L.Get("General"), L.Get("AirFlash streaming"), L.Get("Audio capture"), L.Get("Receivers"), L.Get("Monitor"), L.Get("About")];
    public string PageTitle => Pages[Math.Clamp(SelectedPage, 0, Pages.Length - 1)];
    public string PageDescription => new[] { L.Get("Customize startup and appearance"), L.Get("Balance responsiveness and connection stability"), L.Get("Choose the system audio to send to HomePod"), L.Get("Manage receivers, connections and per-device settings"), L.Get("Live statistics for the current session"), L.Get("Windows audio, wirelessly to HomePod") }[Math.Clamp(SelectedPage, 0, 5)];
    public ObservableCollection<AudioEndpoint> Endpoints { get; } = [];
    public ObservableCollection<ReceiverEditor> Receivers { get; } = [];
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand RefreshEndpointsCommand { get; }
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
        ApplyCommand = new(async () => { await ApplyAsync(); }, ShowError, CanApply);
        OkCommand = new(async () => { if (!HasChanges || await ApplyAsync()) CloseRequested?.Invoke(true); }, ShowError, () => CanEdit && _validationErrors == 0 && _validation is null);
        CancelCommand = new(() => { if (CanEdit) CloseRequested?.Invoke(false); }, () => CanEdit);
        RefreshEndpointsCommand = new(() => LoadEndpointsAsync(true), ShowError, () => CanEdit && !_endpointLoading);
        OpenLogsCommand = new(() => { try { AppPaths.OpenLogs(); } catch (Exception error) { ShowError(error); } });
        CopyDiagnosticsCommand = new(() => { try { Clipboard.SetText(App.Diagnostics()); } catch (Exception error) { ShowError(error); } });
        AddReceiverCommand = new(() => { if (CanEdit) AddReceiverRequested?.Invoke(); }, () => CanEdit);
        LicensesCommand = new(() => new Ui.LicensesWindow().Show());
        Draft.PropertyChanged += DraftChanged;
        Draft.ManualReceivers.CollectionChanged += ManualChanged;
        RefreshCatalog(); RefreshValidation();
        App.CatalogChanged += RefreshCatalog;
        App.Endpoints.Changed += UpdateEndpoints;
        UpdateEndpoints();
    }
    private void SyncSubscriptions()
    {
        foreach (var options in _subscribed.Where(o => !Draft.Receivers.ContainsValue(o)).ToArray()) { options.PropertyChanged -= DraftChanged; _subscribed.Remove(options); }
        foreach (var options in Draft.Receivers.Values) if (_subscribed.Add(options)) options.PropertyChanged += DraftChanged;
    }
    private void ManualChanged(object? sender, NotifyCollectionChangedEventArgs args) { if (!_updating) RefreshValidation(); }
    private void DraftChanged(object? sender, PropertyChangedEventArgs args) { if (!_updating) RefreshValidation(); }
    private void RefreshValidation(bool clearError = true)
    {
        if (_updating || _disposed) return;
        _validation = Draft.Validate();
        _hasChanges = !SettingsMerge.Equal(_baseline, Draft);
        if (clearError) Error = _validationErrors > 0 ? L.Get("Correct the highlighted fields before applying.") : _validation ?? "";
        Notify(nameof(HasChanges)); ApplyCommand.Refresh(); OkCommand.Refresh();
    }
    public void SetValidationErrors(int count) { _validationErrors = Math.Max(0, count); RefreshValidation(); }
    private bool CanApply() => CanEdit && _validationErrors == 0 && _validation is null && HasChanges;
    private void RefreshBusy()
    {
        Notify(nameof(IsApplying)); Notify(nameof(CanEdit));
        ApplyCommand.Refresh(); OkCommand.Refresh(); CancelCommand.Refresh(); AddReceiverCommand.Refresh(); RefreshEndpointsCommand.Refresh();
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
    private void RefreshCatalog()
    {
        if (_disposed) return;
        var manualIds = Draft.ManualReceivers.Select(r => r.Id).ToHashSet();
        var catalog = App.AllReceivers.Where(r => !r.IsManual || manualIds.Contains(r.Id)).ToDictionary(r => r.Id);
        foreach (var manual in Draft.ManualReceivers) catalog[manual.Id] = new(manual.Id, manual.Name, manual.Host, manual.Port) { IsManual = true };
        foreach (var id in Draft.Receivers.Keys) catalog.TryAdd(id, new(id, L.Get("Offline receiver"), id) { Online = false });
        foreach (var row in Receivers.Where(r => !catalog.ContainsKey(r.Receiver.Id)).ToArray()) Receivers.Remove(row);
        var index = 0;
        foreach (var receiver in catalog.Values.OrderBy(r => r.Name))
        {
            var options = Draft.Options(receiver.Id);
            _baseline.Options(receiver.Id);
            var row = Receivers.FirstOrDefault(r => r.Receiver.Id == receiver.Id);
            if (row is null)
            {
                row = new(receiver, options, current => PairRequested?.Invoke(current), () => RemoveManual(receiver.Id), () => CanEdit);
                Receivers.Insert(index, row);
            }
            else
            {
                row.SetOptions(options);
                if (!AppViewModel.ReceiverEqual(row.Receiver, receiver)) { row.Receiver = receiver; row.Refresh(); }
                var oldIndex = Receivers.IndexOf(row); if (oldIndex != index) Receivers.Move(oldIndex, index);
            }
            index++;
        }
        SyncSubscriptions();
        RefreshValidation(false);
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
        var row = Receivers.FirstOrDefault(r => r.Receiver.Id == id); if (row is not null) Receivers.Remove(row);
        SyncSubscriptions(); RefreshValidation();
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
    private void ShowError(Exception error) { Error = error.Message; AppPaths.Log(error.ToString()); }
    public void Dispose()
    {
        Updates.Dispose(); _disposed = true; App.CatalogChanged -= RefreshCatalog; App.Endpoints.Changed -= UpdateEndpoints;
        App.SetMonitorVisible(false);
        Draft.PropertyChanged -= DraftChanged; Draft.ManualReceivers.CollectionChanged -= ManualChanged;
        foreach (var options in _subscribed) options.PropertyChanged -= DraftChanged;
        _subscribed.Clear();
    }
}
public sealed class ReceiverEditor : ObservableObject
{
    public Receiver Receiver { get; set; }
    public ReceiverOptions Options { get; private set; }
    public void SetOptions(ReceiverOptions options) { if (ReferenceEquals(Options, options)) return; Options = options; Notify(nameof(Options)); }
    public ReceiverEditor(Receiver receiver, ReceiverOptions options, Action<Receiver> pair, Action remove, Func<bool> canEdit)
    {
        Receiver = receiver; Options = options;
        PairCommand = new(() => { if (canEdit()) pair(Receiver); }, () => canEdit() && Receiver.Online && Receiver.Complete);
        RemoveCommand = new(() => { if (canEdit()) remove(); }, canEdit);
    }
    public string Name => Receiver.Name;
    public string Detail => $"{Receiver.Address}:{Receiver.Port} · {(Receiver.Online ? Receiver.Detail : L.Get("Offline"))}";
    public bool IsManual => Receiver.IsManual;
    public RelayCommand PairCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public void Refresh() { Notify(nameof(Name)); Notify(nameof(Detail)); Notify(nameof(IsManual)); PairCommand.Refresh(); RemoveCommand.Refresh(); }
}
