using System.Globalization;
using System.Text.Json;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.Verification;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App;

public partial class App : Application
{
    private readonly Exception? _verificationCultureError;
    public App()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            AppPaths.Log($"Dispatcher failure: {eventArgs.Exception}");
            eventArgs.Handled = true;
            if (_viewModel is not null) _viewModel.ShowError(eventArgs.Exception);
            else
            {
                var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
                if (Program.IsDiagnostic(args))
                {
                    try { WriteOutput(args, new { ok = false, stage = "wpf-dispatcher", error = eventArgs.Exception.ToString() }); }
                    catch (Exception error) { AppPaths.Log($"Diagnostic output failed: {error.Message}"); }
                }
                else MessageBox.Show(eventArgs.Exception.Message + "\n\n" + AppPaths.LogDirectory,
                    "AirFlash startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        var args = Environment.GetCommandLineArgs()[1..];
        if (!Program.StartupOptions(args).Any(a => a is "--ui-smoke" or "--tray-smoke" or "--ui-perf")) return;
        try
        {
            if (Program.StartupValue(args, "--ui-culture") is not { } cultureName) return;
            var culture = CultureInfo.GetCultureInfo(cultureName);
            // Capture the requested verification culture before async startup.
            // This process-local selection never changes Windows regional settings.
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (Exception error) { _verificationCultureError = error; }
    }
    private SingleInstance? _instance;
    private ThemeService? _theme;
    private AppViewModel? _viewModel;
    private ControlPanel? _panel;
    private SettingsWindow? _settings;
    private MuteRecoveryWindow? _muteRecovery;
    private TrayService? _tray;
    private EngineInitialization? _engine;
    private bool _quitting;
    private string _uiLanguage = "system";
    private string _uiTheme = "system";
    protected override async void OnStartup(StartupEventArgs args)
    {
        // Initialize before any windows, view models or static choice labels are created.
        var options = Program.StartupOptions(args.Args).ToHashSet(StringComparer.Ordinal);
        var smokeLanguage = options.Any(option => option is "--ui-smoke" or "--tray-smoke" or "--ui-perf")
            && Program.StartupValue(args.Args, "--ui-language") is { } language ? new[] { language } : null;
        try
        {
            if (_verificationCultureError is { } cultureError)
            {
                WriteOutput(args.Args, new { ok = false, error = cultureError.ToString() });
                Shutdown(1); return;
            }
            L.Initialize(smokeLanguage);
            base.OnStartup(args);
            Resources["LatencyChoices"] = Choices.Latencies;
            if (options.Contains("--discovery-perf"))
            {
                var output = Program.StartupValue(args.Args, "--output") ?? throw new ArgumentException("--discovery-perf requires --output.");
                AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "isolated-data");
                Shutdown(await DiscoveryPerf.RunAsync(args.Args)); return;
            }
            if (options.Contains("--update-check"))
            {
                using var client = new System.Net.Http.HttpClient { MaxResponseContentBufferSize = 1024 * 1024 };
                var release = await new UpdateService(client).CheckAsync();
                WriteOutput(args.Args, new { ok = true, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, latest = release?.Version.ToString(3), download = release?.DownloadPage.AbsoluteUri });
                Shutdown(0); return;
            }
            if (options.Contains("--self-check")) { Shutdown(await SelfCheckAsync(args.Args)); return; }
            if (options.Contains("--stability-check")) { Shutdown(await StabilityCheck.RunAsync(args.Args)); return; }
            if (options.Contains("--compat-check")) { Shutdown(await CompatibilityCheckAsync(args.Args)); return; }
            if (options.Contains("--discovery-check")) { Shutdown(await DiscoveryCheckAsync(args.Args)); return; }
            if (options.Contains("--audio-check")) { Shutdown(await AudioCheckAsync(args.Args)); return; }
            if (options.Contains("--ui-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            if (options.Contains("--tray-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            if (options.Contains("--ui-perf")) { Shutdown(await UiPerf.RunAsync(args.Args)); return; }
            _instance = new();
            if (!_instance.IsOwner) { await _instance.NotifyExistingAsync(); await _instance.DisposeAsync(); _instance = null; Shutdown(); return; }
            _theme = new();
            var store = new SettingsStore(Path.Combine(AppPaths.DataDirectory, "config.json"));
            var initial = store.Load();
            _uiLanguage = initial.UiLanguage;
            _uiTheme = initial.Theme;
            _theme.SetPreference(_uiTheme);
            L.Initialize(_uiLanguage == "system" ? null : [_uiLanguage]);
            Resources["LatencyChoices"] = Choices.Latencies;
            var audio = new AudioService();
            var factory = new ExtractingEngineFactory();
            _engine = new(token => QueryEngineAsync(factory, token), AppPaths.Log);
            _viewModel = new(store, new WindowsDiscovery(log: AppPaths.Log), new Autostart(), audio, factory, Dispatcher, _engine);
            _viewModel.ResetCompleted += Quit;
            _panel = new(_viewModel); MainWindow = _panel;
            AttachPanel(_panel);
            _tray = new(_panel, _viewModel, ShowSettings, Quit); _panel.Tray = _tray;
            _instance.Listen(() => Dispatcher.BeginInvoke(() =>
            {
                if (_quitting) return;
                if (_settings is not null) _settings.Activate();
                else _panel.ShowPanel();
            }));
            _viewModel.SettingsChanged += OnSettingsChanged;
            _viewModel.MuteRecoveryRequested += ShowMuteRecovery;
            _viewModel.Start();
            if (!options.Contains("--startup")) _panel.ShowPanel();
            _ = _viewModel.CheckUpdatesAtStartupAsync();
            AppPaths.Log("WPF host started, JSONL v1. Startup stage=shell ready");
            await _engine.InitializeAsync();
        }
        catch (Exception error)
        {
            AppPaths.Log(error.ToString());
            if (Program.IsDiagnostic(args.Args)) { WriteOutput(args.Args, new { ok = false, error = error.ToString() }); Shutdown(1); return; }
            MessageBox.Show(error.Message + "\n\n" + AppPaths.LogDirectory, L.Get("AirFlash startup failed"), MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }
    private void OnSettingsChanged()
    {
        if (_viewModel is null) return;
        if (_uiTheme != _viewModel.Settings.Theme) { _uiTheme = _viewModel.Settings.Theme; _theme?.SetPreference(_uiTheme); }
        if (_uiLanguage == _viewModel.Settings.UiLanguage) return;
        _uiLanguage = _viewModel.Settings.UiLanguage;
        // Wait for the settings transaction, including Apply/OK, before replacing windows.
        Dispatcher.BeginInvoke(async () =>
        {
            if (_settings is { } applying) await applying.ViewModel.ApplicationCompleted;
            if (_quitting || _viewModel is null) return;
            L.Initialize(_uiLanguage == "system" ? null : [_uiLanguage]);
            Resources["LatencyChoices"] = Choices.Latencies;
            _viewModel.RefreshLatencyChoices();
            var showSettings = _settings?.IsVisible == true;
            var page = _settings?.ViewModel.SelectedPage ?? 0;
            var showPanel = _panel?.IsVisible == true;
            _muteRecovery?.Close();
            _settings?.Close();
            _tray?.Dispose(); _panel?.ShutdownPanel();
            _panel = new(_viewModel); MainWindow = _panel;
            AttachPanel(_panel);
            _tray = new(_panel, _viewModel, ShowSettings, Quit); _panel.Tray = _tray;
            foreach (var row in _viewModel.Receivers) row.Refresh();
            if (showSettings) { ShowSettings(); _settings!.ViewModel.SelectedPage = page; }
            else if (showPanel) _panel.ShowPanel();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void AttachPanel(ControlPanel panel)
    {
        panel.SettingsRequested += ShowSettings;
        panel.LatencySettingsRequested += ShowSettingsPage;
        panel.UpdateRequested += ShowAbout;
        panel.QuitRequested += Quit;
    }
    private void ShowAbout() => ShowSettingsPage(SettingsViewModel.AboutPage);
    private void ShowSettings() => ShowSettings(null);
    private void ShowSettingsPage(int page) => ShowSettings(page);
    private void ShowSettings(int? page)
    {
        if (_quitting || _viewModel is null) return;
        _panel?.Hide();
        if (_settings is not null)
        {
            if (page is { } selected) _settings.ViewModel.SelectedPage = selected;
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate(); return;
        }
        _settings = new(_viewModel); _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        if (page is { } initial) _settings.ViewModel.SelectedPage = initial;
        _settings.Activate();
    }
    private void ShowMuteRecovery()
    {
        if (_quitting || _viewModel is null) return;
        if (_muteRecovery is not null) { _muteRecovery.Activate(); return; }
        _muteRecovery = new(_viewModel);
        _muteRecovery.Closed += (_, _) => _muteRecovery = null;
        _muteRecovery.Show(); _muteRecovery.Activate();
    }
    private async void Quit()
    {
        if (_quitting) return; _quitting = true;
        try
        {
            await ShellShutdown.RunAsync(() => _viewModel?.BeginShutdown(),
                () => _settings?.ViewModel.ApplicationCompleted ?? Task.CompletedTask,
                ShellShutdown.Sync(() => _settings?.Close()),
                ShellShutdown.Sync(() => _muteRecovery?.Close()),
                () => _engine?.DisposeAsync() ?? ValueTask.CompletedTask,
                () => _viewModel?.DisposeAsync() ?? ValueTask.CompletedTask,
                ShellShutdown.Sync(() => _tray?.Dispose()),
                ShellShutdown.Sync(() => _panel?.ShutdownPanel()),
                () => _instance?.DisposeAsync() ?? ValueTask.CompletedTask,
                ShellShutdown.Sync(() => _theme?.Dispose()));
        }
        catch (Exception error) { AppPaths.Log(error.ToString()); }
        finally { Shutdown(); }
    }
    internal static async Task<string> QueryEngineAsync(IEngineFactory factory, CancellationToken cancellation = default) => (await QueryHelloAsync(factory, cancellation)).Text("engine_version", L.Get("Unknown"));
    private static async Task<JsonElement> QueryHelloAsync(IEngineFactory factory, CancellationToken cancellation = default)
    {
        return await EngineHandshake.HelloAsync(factory, cancellation);
    }
    private sealed class ExtractingEngineFactory : IEngineFactory
    {
        public IEngineConnection Open()
        {
            AppPaths.Log("Startup stage=engine-extract started");
            var executable = AppPaths.ExtractEngine();
            AppPaths.Log("Startup stage=engine-extract ready");
            return new ProcessEngineFactory(executable, AppPaths.Log).Open();
        }
    }
    private static async Task<int> CompatibilityCheckAsync(string[] args)
    {
        var capabilities = WindowsCapabilities.Current;
        var stage = "engine-extract";
        try
        {
            var executable = AppPaths.ExtractEngine();
            stage = "engine-hello";
            var hello = await QueryHelloAsync(new ProcessEngineFactory(executable, AppPaths.Log));
            WriteOutput(args, new { ok = capabilities.Supported && capabilities.DnsSdAvailable, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, capabilities,
                requirement = WindowsCompatibility.Requirement, native = hello,
                qualification = "Win10 execution pending; this check does not exercise discovery, pairing or playback." });
            return capabilities.Supported && capabilities.DnsSdAvailable ? 0 : 1;
        }
        catch (Exception error)
        {
            AppPaths.Log($"Compatibility stage={stage} failed {error}");
            WriteOutput(args, new { ok = false, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, capabilities, stage, error = error.ToString(), hresult = $"0x{error.HResult:X8}" });
            return 1;
        }
    }
    private static async Task<int> SelfCheckAsync(string[] args)
    {
        var hello = await QueryHelloAsync(new ProcessEngineFactory(AppPaths.ExtractEngine()));
        WriteOutput(args, new { ok = true, host = "wpf", version = AppPaths.Version, display_version = AppPaths.DisplayVersion, native = hello });
        return 0;
    }
    private static async Task<int> AudioCheckAsync(string[] args)
    {
        await using var audio = new AudioService();
        var endpoints = await audio.GetEndpointsAsync();
        var defaultId = await audio.GetDefaultEndpointIdAsync();
        WriteOutput(args, new { ok = true, endpoints = endpoints.Count, devices = endpoints.Select(e => new { e.Id, e.Name, kind = e.Kind.ToString() }), default_endpoint_found = endpoints.Any(e => e.Id == defaultId), note = "Read-only MTA audio enumeration; no playback or mute changes." });
        return 0;
    }
    private static async Task<int> DiscoveryCheckAsync(string[] args)
    {
        using var discovery = new WindowsDiscovery();
        return await RunDiscoveryCheckAsync(discovery, () => Task.Delay(TimeSpan.FromSeconds(8)), report => WriteOutput(args, report));
    }
    internal static async Task<int> RunDiscoveryCheckAsync(IDiscoveryService discovery, Func<Task> observe, Action<object> writeReport)
    {
        var gate = new object();
        IReadOnlyList<Receiver> receivers = [];
        var errors = new List<string>();
        void Changed(IReadOnlyList<Receiver> value) { lock (gate) receivers = value; }
        void Failed(DiscoveryFailure failure) { lock (gate) errors.Add(failure.Message); }
        discovery.Changed += Changed;
        discovery.Failed += Failed;
        try
        {
            discovery.Start();
            await observe();
            // DNS callbacks can still arrive while the report is being written.
            Receiver[] observedReceivers;
            string[] observedErrors;
            lock (gate)
            {
                observedReceivers = receivers.ToArray();
                observedErrors = errors.ToArray();
            }
            var ok = observedErrors.Length == 0;
            writeReport(new { ok, receivers = observedReceivers, errors = observedErrors });
            return ok ? 0 : 1;
        }
        finally
        {
            discovery.Changed -= Changed;
            discovery.Failed -= Failed;
        }
    }
    internal static void WriteOutput(string[] args, object value, JsonSerializerOptions? options = null)
    {
        if (Program.StartupValue(args, "--output", "--self-check-output") is { } path)
        {
            var output = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(value, options ?? new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

