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
    public App()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            AppPaths.Log($"Dispatcher failure: {eventArgs.Exception}");
            eventArgs.Handled = true;
            if (_viewModel is not null) _viewModel.ShowError(eventArgs.Exception);
            else
            {
                MessageBox.Show(eventArgs.Exception.Message + "\n\n" + AppPaths.LogDirectory,
                    "AirFlash startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
    }
    private SingleInstance? _instance;
    private ThemeService? _theme;
    private AppViewModel? _viewModel;
    private ControlPanel? _panel;
    private SettingsWindow? _settings;
    private TrayService? _tray;
    private EngineInitialization? _engine;
    private bool _quitting;
    private string _uiLanguage = "system";
    private string _uiTheme = "system";
    protected override async void OnStartup(StartupEventArgs args)
    {
        // Initialize before any windows, view models or static choice labels are created.
        var languageIndex = Array.IndexOf(args.Args, "--ui-language");
        var smokeLanguage = (args.Args.Contains("--ui-smoke") || args.Args.Contains("--tray-smoke") || args.Args.Contains("--ui-perf")) && languageIndex >= 0 && languageIndex + 1 < args.Args.Length
            ? new[] { args.Args[languageIndex + 1] } : null;
        try
        {
            L.Initialize(smokeLanguage);
            base.OnStartup(args);
            Resources["LatencyChoices"] = Choices.Latencies;
            if (args.Args.Contains("--update-check"))
            {
                using var client = new System.Net.Http.HttpClient { MaxResponseContentBufferSize = 1024 * 1024 };
                var release = await new UpdateService(client).CheckAsync();
                WriteOutput(args.Args, new { ok = true, version = AppPaths.Version, latest = release?.Version.ToString(3), download = release?.DownloadPage.AbsoluteUri });
                Shutdown(0); return;
            }
            if (args.Args.Contains("--self-check")) { Shutdown(await SelfCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--compat-check")) { Shutdown(await CompatibilityCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--discovery-check")) { Shutdown(await DiscoveryCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--audio-check")) { Shutdown(await AudioCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--ui-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            if (args.Args.Contains("--tray-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            if (args.Args.Contains("--ui-perf")) { Shutdown(await UiPerf.RunAsync(args.Args)); return; }
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
            _viewModel = new(store, new WindowsDiscovery(), new Autostart(), audio, factory, Dispatcher, _engine);
            _panel = new(_viewModel); MainWindow = _panel;
            _panel.SettingsRequested += ShowSettings; _panel.QuitRequested += Quit;
            _tray = new(_panel, _viewModel, ShowSettings, Quit); _panel.Tray = _tray;
            _instance.Listen(() => Dispatcher.BeginInvoke(() => { if (_settings is not null) _settings.Activate(); else _panel.ShowPanel(); }));
            _viewModel.SettingsChanged += OnSettingsChanged;
            _viewModel.Start();
            if (!args.Args.Contains("--startup")) _panel.ShowPanel();
            AppPaths.Log("WPF host started, JSONL v1. Startup stage=shell ready");
            await _engine.InitializeAsync();
        }
        catch (Exception error)
        {
            AppPaths.Log(error.ToString());
            if (args.Args.Any(a => a.EndsWith("-check", StringComparison.Ordinal) || a is "--ui-smoke" or "--tray-smoke" or "--ui-perf")) { WriteOutput(args.Args, new { ok = false, error = error.ToString() }); Shutdown(1); return; }
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
            var showSettings = _settings?.IsVisible == true;
            var page = _settings?.ViewModel.SelectedPage ?? 0;
            var showPanel = _panel?.IsVisible == true;
            _settings?.Close();
            _tray?.Dispose(); _panel?.ShutdownPanel();
            _panel = new(_viewModel); MainWindow = _panel;
            _panel.SettingsRequested += ShowSettings; _panel.QuitRequested += Quit;
            _tray = new(_panel, _viewModel, ShowSettings, Quit); _panel.Tray = _tray;
            foreach (var row in _viewModel.Receivers) row.Refresh();
            if (showSettings) { ShowSettings(); _settings!.ViewModel.SelectedPage = page; }
            else if (showPanel) _panel.ShowPanel();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void ShowSettings()
    {
        if (_viewModel is null) return;
        _panel?.Hide();
        if (_settings is not null) { if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal; _settings.Activate(); return; }
        _settings = new(_viewModel); _settings.Closed += (_, _) => _settings = null; _settings.Show(); _settings.Activate();
    }
    private async void Quit()
    {
        if (_quitting) return; _quitting = true;
        try
        {
            if (_settings is { } applying) await applying.ViewModel.ApplicationCompleted;
            _settings?.Close();
            if (_engine is not null) await _engine.DisposeAsync();
            if (_viewModel is not null) await _viewModel.DisposeAsync();
            _tray?.Dispose(); _panel?.ShutdownPanel();
            if (_instance is not null) await _instance.DisposeAsync();
            _theme?.Dispose();
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
            WriteOutput(args, new { ok = capabilities.Supported && capabilities.DnsSdAvailable, version = AppPaths.Version, capabilities,
                requirement = WindowsCompatibility.Requirement, native = hello,
                qualification = "Win10 execution pending; this check does not exercise discovery, pairing or playback." });
            return capabilities.Supported && capabilities.DnsSdAvailable ? 0 : 1;
        }
        catch (Exception error)
        {
            AppPaths.Log($"Compatibility stage={stage} failed {error}");
            WriteOutput(args, new { ok = false, version = AppPaths.Version, capabilities, stage, error = error.ToString(), hresult = $"0x{error.HResult:X8}" });
            return 1;
        }
    }
    private static async Task<int> SelfCheckAsync(string[] args)
    {
        var hello = await QueryHelloAsync(new ProcessEngineFactory(AppPaths.ExtractEngine()));
        WriteOutput(args, new { ok = true, host = "wpf", version = AppPaths.Version, native = hello });
        return 0;
    }
    private static async Task<int> AudioCheckAsync(string[] args)
    {
        await using var audio = new AudioService();
        var endpoints = await audio.GetEndpointsAsync();
        var defaultId = await audio.GetDefaultEndpointIdAsync();
        WriteOutput(args, new { ok = true, endpoints = endpoints.Count, default_endpoint_found = endpoints.Any(e => e.Id == defaultId), note = "Read-only MTA audio enumeration; no playback or mute changes." });
        return 0;
    }
    private static async Task<int> DiscoveryCheckAsync(string[] args)
    {
        using var discovery = new WindowsDiscovery();
        IReadOnlyList<Receiver> receivers = []; var errors = new List<string>();
        discovery.Changed += value => receivers = value;
        discovery.Failed += error => { lock (errors) errors.Add(error); };
        discovery.Start(); await Task.Delay(TimeSpan.FromSeconds(8));
        WriteOutput(args, new { ok = errors.Count == 0, receivers, errors }); return errors.Count == 0 ? 0 : 1;
    }
    internal static void WriteOutput(string[] args, object value, JsonSerializerOptions? options = null)
    {
        var index = Array.FindIndex(args, a => a is "--self-check-output" or "--output");
        if (index >= 0 && index + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(value, options ?? new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

