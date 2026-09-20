using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;
// Opt-in, isolated UI verification. Never constructs real discovery, audio or autostart services.
internal static class UiSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        var report = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Path.GetTempPath(), "airplay-ui-smoke", "report.json");
        var directory = Path.GetDirectoryName(report)!; Directory.CreateDirectory(directory);
        AppPaths.DataDirectory = Path.Combine(directory, "isolated-data");
        var checks = new List<string>();
        var store = new MemoryStore(); var engine = new MockFactory();
        await using var app = new AppViewModel(store, new MockDiscovery(), new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher) { EngineVersion = "0.1.0（模拟）" };
        var panel = new ControlPanel(app); SettingsWindow? settings = null;
        using var tray = new TrayService(panel, app, () => { }, () => { }); panel.Tray = tray;
        try
        {
            var instanceName = "verification-" + Guid.NewGuid().ToString("N");
            await using (var firstInstance = new SingleInstance(instanceName))
            await using (var secondInstance = new SingleInstance(instanceName))
            {
                var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstInstance.Listen(() => activated.TrySetResult());
                Check(firstInstance.IsOwner && !secondInstance.IsOwner, "single instance mutex ownership", checks);
                await secondInstance.NotifyExistingAsync(); await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
                checks.Add("second instance activation via current-user pipe");
            }
            ThemeService.SetForVerification(false); app.Start(); panel.ShowPanel(); await Pump();
            Check(app.Receivers.Count == 2 && app.Receivers.All(r => r.Receiver.Online), "offline receivers excluded", checks);
            var area = tray.WorkArea(); GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out var bounds);
            Check(Math.Abs(bounds.Right - (area.Rect.Right - Math.Round(12 * area.Scale))) <= 2 && Math.Abs(bounds.Bottom - (area.Rect.Bottom - Math.Round(12 * area.Scale))) <= 2, "native panel aligns to monitor work area", checks);
            SendMessage(tray.WindowHandle, 0x8001, IntPtr.Zero, new(0x10400)); await Pump();
            Check(!panel.IsVisible, "tray activation hides panel", checks);
            await Task.Delay(280); SendMessage(tray.WindowHandle, 0x8001, IntPtr.Zero, new(0x10400)); await Pump();
            Check(panel.IsVisible, "tray activation opens panel", checks);
            var mode = Descendants(panel).OfType<ComboBox>().Single(); mode.IsDropDownOpen = true; await Pump();
            Check(panel.IsVisible, "dropdown does not dismiss panel", checks); mode.IsDropDownOpen = false; await Pump();
            app.Receivers[0].ToggleCommand.Execute(null);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            Check(engine.Commands.Any(c => c.Text("command") == "start"), "panel start reaches engine", checks);
            app.MasterVolume = 25; await app.FlushVolumeAsync();
            Check(engine.Commands.Any(c => c.Text("command") == "set_gain" && c.GetProperty("params").Number("gain") == .25), "master gain reaches engine", checks);
            settings = new(app); settings.Show(); await Pump();
            Check(!settings.ApplyButton.IsEnabled, "apply initially disabled", checks);
            settings.ViewModel.SelectedPage = 2; await Pump();
            var radio = Descendants(settings).OfType<RadioButton>().Single(r => (string?)r.Content == L.Get("Specific output endpoint"));
            var radioPeer = UIElementAutomationPeer.CreatePeerForElement(radio)!;
            ((ISelectionItemProvider)radioPeer.GetPattern(PatternInterface.SelectionItem)!).Select();
            await Pump();
            var endpoints = Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "EndpointCombo");
            Check(endpoints.IsEnabled, "capture mode enables endpoint", checks);
            endpoints.SelectedIndex = 1; await Pump();
            var startsBefore = engine.Commands.Count(c => c.Text("command") == "start");
            Invoke(settings.ApplyButton); await Until(() => app.Settings.CaptureMode == "endpoint" && !settings.ViewModel.HasChanges);
            await Until(() => engine.Commands.Count(c => c.Text("command") == "start") > startsBefore);
            Check(engine.Commands.Count(c => c.Text("command") == "start") == startsBefore + 1, "settings restart once", checks);
            settings.ViewModel.Draft.StartAtLogin = true; store.Fail = true;
            Invoke(settings.ApplyButton); await Until(() => settings.ViewModel.Error.Contains("模拟保存失败", StringComparison.Ordinal));
            Check(settings.IsVisible && settings.ViewModel.HasChanges && !app.Settings.StartAtLogin, "failed save preserves draft", checks);
            store.Fail = false; Invoke(settings.ApplyButton); await Until(() => !settings.ViewModel.HasChanges);
            settings.ViewModel.Draft.StartAtLogin = false;
            settings.ViewModel.CancelCommand.Execute(null); await Pump();
            Check(app.Settings.StartAtLogin, "cancel keeps last applied state", checks);
            settings = new(app); settings.Show(); await Pump();
            settings.ViewModel.SelectedPage = 0; await Pump();
            var retries = Descendants(settings).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == L.Get("Maximum retry attempts"));
            retries.Text = "invalid"; await Pump();
            settings.ViewModel.SelectedPage = 2; await Pump(); settings.ViewModel.SelectedPage = 0; await Pump();
            Check(retries.Text == "invalid", "invalid edits survive page switches", checks);
            Check(!settings.ApplyButton.IsEnabled && !settings.OkButton.IsEnabled, "invalid number blocks save", checks);
            retries.Text = "5"; await Pump();
            settings.ViewModel.AddManual("书房", "127.0.0.2", 7000); await settings.ViewModel.ApplyAsync();
            Check(app.Settings.ManualReceivers.Count == 1, "manual receiver persists", checks);
            var firstId = app.Receivers[0].Receiver.Id;
            settings.ViewModel.Draft.Options(firstId).Hidden = true; await settings.ViewModel.ApplyAsync();
            Check(app.Receivers.All(r => r.Receiver.Id != firstId) && settings.ViewModel.Receivers.Any(r => r.Receiver.Id == firstId), "hidden receiver stays recoverable", checks);
            settings.ViewModel.Draft.Options(firstId).Hidden = false; await settings.ViewModel.ApplyAsync();
            Check(app.Receivers.Any(r => r.Receiver.Id == firstId), "unhide receiver", checks);
            settings.Close(); settings = new(app); settings.Show(); await Pump();
            Check(settings.ViewModel.Receivers.Count >= 3, "nullable overrides reopen", checks);
            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                CheckThemeColors(dark, checks);
                for (var page = 0; page < 6; page++)
                {
                    settings.ViewModel.SelectedPage = page; await Pump();
                    Render(settings, Path.Combine(directory, $"settings-{(dark ? "dark" : "light")}-{page}.png"), 1);
                    if (page == 4)
                    {
                        var scroll = Descendants(settings).OfType<ScrollViewer>().First(v => v.ScrollableHeight > 0);
                        scroll.ScrollToEnd(); await Pump();
                        Render(settings, Path.Combine(directory, $"monitor-details-{(dark ? "dark" : "light")}.png"), 1);
                        scroll.ScrollToHome();
                    }
                }
                settings.Hide(); panel.ShowPanel(); await Pump();
                foreach (var scale in new[] { 1d, 1.5, 2d }) Render(panel, Path.Combine(directory, $"panel-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
                var quit = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Quit"));
                var quitRequested = false;
                void OnQuit() => quitRequested = true;
                panel.QuitRequested += OnQuit;
                Invoke(quit); await Pump();
                panel.QuitRequested -= OnQuit;
                Check(quitRequested && quit.ContextMenu is null, "exit icon requests quit directly", checks);
                settings.Show();
            }
            Check(app.MonitorMembers.Count == 1 && app.MonitorRecoveries == "2", "transport metrics reach monitoring view", checks);
            checks.Add("six pages rendered in light/dark; panel at 100/150/200 percent");
            await app.StopAsync(); await Until(() => app.Snapshot.State == PlaybackState.Idle);
            Check(engine.DisposedCount == engine.CreatedCount, "stop releases mock engine", checks);
            await UiRegression.RunAsync(checks, directory);
            App.WriteOutput(args, new { ok = true, checks, note = "All engine/audio/discovery/autostart services are simulated. DPI renders do not replace physical multimonitor QA." });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        finally { settings?.Close(); panel.ShutdownPanel(); }
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeWindowPlacement.Rectangle rectangle);
    private static void Check(bool value, string message, List<string> checks) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private static void CheckThemeColors(bool dark, List<string> checks)
    {
        var expected = dark ? Color.FromRgb(0x78, 0xA9, 0xFF) : Color.FromRgb(0x25, 0x63, 0xEB);
        Check(ResourceColor("AccentBrush") == expected, $"{(dark ? "dark" : "light")} custom accent is blue", checks);
        Check(ResourceColor("AccentFillColorDefaultBrush") == expected, $"{(dark ? "dark" : "light")} Fluent accent is blue", checks);
        Check(ResourceColor("SystemColorHighlightColorBrush") == expected, $"{(dark ? "dark" : "light")} highlight is blue", checks);
        Check(ResourceColor("ProgressBarForeground") == expected, $"{(dark ? "dark" : "light")} progress accent is blue", checks);
        Check(ResourceColor("SliderThumbBackground") == expected, $"{(dark ? "dark" : "light")} slider accent is blue", checks);
        Check(ResourceColor("SystemColorWindowColorBrush") != Colors.Magenta, $"{(dark ? "dark" : "light")} window color is not magenta", checks);
        Check(ResourceColor("SystemColorHighlightTextColorBrush") == (dark ? Color.FromRgb(0x16, 0x22, 0x39) : Colors.White), $"{(dark ? "dark" : "light")} accent text is readable", checks);
    }
    private static Color ResourceColor(string key) => Application.Current.Resources[key] switch
    {
        SolidColorBrush brush => brush.Color,
        Color color => color,
        _ => throw new InvalidOperationException($"Resource '{key}' is not a color or brush.")
    };
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(70); }
    private static async Task Until(Func<bool> predicate)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, limit.Token);
        await Pump();
    }
    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    internal static void Render(FrameworkElement window, string file, double scale)
    {
        window.UpdateLayout();
        var visual = window is Window host ? (FrameworkElement)host.Content : window;
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(file); encoder.Save(output);
    }
    internal sealed class MemoryStore : ISettingsStore
    {
        public bool Fail;
        public int DelayMs;
        public int Saves;
        public TaskCompletionSource? SaveGate { get; set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public AppSettings Saved => _settings.Clone();
        private AppSettings _settings = new() { AutoConnectOnDiscover = false, MuteWhileStreaming = false, ShowWiderVolume = false };
        public AppSettings Load() => _settings.Clone();
        public void Save(AppSettings settings) { UiPerformance.Count("settings.save"); Thread.Sleep(DelayMs); if (Fail) throw new IOException("模拟保存失败"); _settings = settings.Clone(); }
        public async Task SaveAsync(AppSettings settings)
        {
            Interlocked.Increment(ref Saves); SaveEntered?.TrySetResult();
            if (SaveGate is { } gate) await gate.Task;
            await Task.Run(() => Save(settings));
        }
    }
    internal sealed class MockAutostart : IAutostart
    {
        public bool FailEnable { get; set; }
        public bool FailDisable { get; set; }
        public bool Enabled { get; private set; }
        public void Set(bool enabled)
        {
            if (enabled ? FailEnable : FailDisable) throw new IOException("mock startup failed");
            Enabled = enabled;
        }
    }
    internal sealed class MockAudio : IAudioService
    {
        public int DelayMs;
        public event Action? EndpointsChanged;
        public int Enumerations;
        public int ActiveEnumerations, MaxEnumerations;
        public TaskCompletionSource? EnumerationGate { get; set; }
        public TaskCompletionSource? EnumerationEntered { get; set; }
        public IReadOnlyList<AudioEndpoint> Items = [new("endpoint-a", "扬声器 · Realtek Audio"), new("endpoint-b", "虚拟声卡 · VB-CABLE")];
        public void NotifyEndpoints() => EndpointsChanged?.Invoke();
        public string? DefaultEndpoint { get; set; } = "endpoint-a";
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult(DefaultEndpoint);
        public async Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync()
        {
            UiPerformance.Count("audio.enumerate"); Enumerations++;
            ActiveEnumerations++; MaxEnumerations = Math.Max(MaxEnumerations, ActiveEnumerations);
            var items = Items.ToArray(); EnumerationEntered?.TrySetResult();
            try { if (EnumerationGate is { } gate) await gate.Task; await Task.Delay(DelayMs); return items; }
            finally { ActiveEnumerations--; }
        }
        public bool FailMute { get; set; }
        public Task MuteAsync(string? endpointId) => FailMute ? Task.FromException(new IOException("mock mute failed")) : Task.CompletedTask;
        public Task RestoreAsync() => Task.CompletedTask;
    }
    internal sealed class MockDiscovery : IDiscoveryService
    {
        public event Action<IReadOnlyList<Receiver>>? Changed;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() => Publish();
        public IReadOnlyList<Receiver> Items { get; set; } = [
            new("a", "家庭影院", "127.0.0.1") { Model = "HomePod" },
            new("b", "客厅立体声", "127.0.0.2") { StereoId = "pair", Members = [new("left", "左侧", "127.0.0.2"), new("right", "右侧", "127.0.0.3")] },
            new("c", "卧室 HomePod mini", "127.0.0.4") { Online = false, Model = "HomePod mini" }
        ];
        public void Publish() => Changed?.Invoke(Items);
        public void Dispose() { }
    }
    internal sealed class MockFactory : IEngineFactory
    {
        public int StopDelayMs;
        public ConcurrentQueue<JsonElement> Commands { get; } = new();
        public int CreatedCount, DisposedCount;
        public IEngineConnection Open() { UiPerformance.Count("engine.open"); Interlocked.Increment(ref CreatedCount); return new MockConnection(this); }
    }
    private sealed class MockConnection(MockFactory factory) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private CancellationTokenSource? _metrics;
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            factory.Commands.Enqueue(JsonSerializer.SerializeToElement(new { command, @params = parameters }));
            if (command == "start")
            {
                Emit(session, new { @event = "streaming" }); _metrics = new(); var token = _metrics.Token;
                _ = Task.Run(async () => { try { while (!token.IsCancellationRequested) { Emit(session, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 10.8, max_queue_age_ms = 11.8, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } }); Emit(session, new { @event = "transport_metrics", session_uptime_ms = 123456, sender_late_recoveries = 2, skipped_packets = 10, members = new[] { new { host = "192.0.2.1", media = new { packets_sent = 12000, media_send_errors = 1, sync_send_errors = 0, retransmit_requests = 6, retransmits_sent = 5, retransmit_missing = 0, retransmit_expired = 1, retransmit_queue_drops = 0, retransmit_send_errors = 0, receiver_latency_ms = 150, receiver_latency_estimated = true }, health = new { feedback_rtt_ms = 4.2, feedback_failures = 0, feedback_delayed = false } } } }); await Task.Delay(350, token); } } catch (OperationCanceledException) { } }, CancellationToken.None);
            }
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        private void Emit(string session, object payload)
        {
            var json = JsonSerializer.SerializeToNode(payload)!.AsObject(); json["version"] = 1; json["session_id"] = session; _events.Writer.TryWrite(JsonSerializer.SerializeToElement(json));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; }
        }
        public async ValueTask DisposeAsync() { await Task.Delay(factory.StopDelayMs); if (_metrics is not null) { await _metrics.CancelAsync(); _metrics.Dispose(); } _events.Writer.TryComplete(); Interlocked.Increment(ref factory.DisposedCount); }
    }
}
