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
    private SingleInstance? _instance;
    private ThemeService? _theme;
    private AppViewModel? _viewModel;
    private ControlPanel? _panel;
    private SettingsWindow? _settings;
    private TrayService? _tray;
    private bool _quitting;
    private string _uiLanguage = "system";
    private string _uiTheme = "system";
    protected override async void OnStartup(StartupEventArgs args)
    {
        // Initialize before any windows, view models or static choice labels are created.
        var languageIndex = Array.IndexOf(args.Args, "--ui-language");
        var smokeLanguage = (args.Args.Contains("--ui-smoke") || args.Args.Contains("--ui-perf")) && languageIndex >= 0 && languageIndex + 1 < args.Args.Length
            ? new[] { args.Args[languageIndex + 1] } : null;
        L.Initialize(smokeLanguage);
        base.OnStartup(args);
        try
        {
            Resources["LatencyChoices"] = Choices.Latencies;
            if (args.Args.Contains("--update-check"))
            {
                using var client = new System.Net.Http.HttpClient { MaxResponseContentBufferSize = 1024 * 1024 };
                var release = await new UpdateService(client).CheckAsync();
                WriteOutput(args.Args, new { ok = true, version = AppPaths.Version, latest = release?.Version.ToString(3), download = release?.DownloadPage.AbsoluteUri });
                Shutdown(0); return;
            }
            if (args.Args.Contains("--self-check")) { Shutdown(await SelfCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--discovery-check")) { Shutdown(await DiscoveryCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--audio-check")) { Shutdown(await AudioCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--ui-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
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
            var factory = new ProcessEngineFactory(AppPaths.ExtractEngine(), AppPaths.Log);
            _viewModel = new(store, new WindowsDiscovery(), new Autostart(), audio, factory, Dispatcher);
            _viewModel.EngineVersion = await QueryEngineAsync(factory);
            _panel = new(_viewModel); MainWindow = _panel;
            _panel.SettingsRequested += ShowSettings; _panel.QuitRequested += Quit;
            _tray = new(_panel, _viewModel, ShowSettings, Quit); _panel.Tray = _tray;
            _instance.Listen(() => Dispatcher.BeginInvoke(() => { if (_settings is not null) _settings.Activate(); else _panel.ShowPanel(); }));
            DispatcherUnhandledException += (_, eventArgs) => { AppPaths.Log(eventArgs.Exception.ToString()); _viewModel.ShowError(eventArgs.Exception); eventArgs.Handled = true; };
            _viewModel.SettingsChanged += OnSettingsChanged;
            _viewModel.Start();
            if (!args.Args.Contains("--startup")) _panel.ShowPanel();
            AppPaths.Log("WPF host started, JSONL v1.");
        }
        catch (Exception error)
        {
            AppPaths.Log(error.ToString());
            if (args.Args.Any(a => a.EndsWith("-check", StringComparison.Ordinal) || a is "--ui-smoke" or "--ui-perf")) { WriteOutput(args.Args, new { ok = false, error = error.ToString() }); Shutdown(1); return; }
            MessageBox.Show(error.Message, L.Get("AirFlash startup failed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_viewModel is not null) await _viewModel.DisposeAsync();
            _tray?.Dispose(); _panel?.ShutdownPanel();
            if (_instance is not null) await _instance.DisposeAsync();
            _theme?.Dispose();
        }
        catch (Exception error) { AppPaths.Log(error.ToString()); }
        finally { Shutdown(); }
    }
    internal static async Task<string> QueryEngineAsync(IEngineFactory factory) => (await QueryHelloAsync(factory)).Text("engine_version", L.Get("Unknown"));
    private static async Task<JsonElement> QueryHelloAsync(IEngineFactory factory)
    {
        await using var connection = factory.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await connection.SendAsync("check", "hello", null, timeout.Token);
        var hello = await connection.ReadAsync(timeout.Token) ?? throw new IOException(L.Get("The engine did not return hello."));
        if (hello.Text("event") != "hello" || hello.Number("version") != 1) throw new IOException(L.Get("Incompatible engine protocol version."));
        return hello;
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

