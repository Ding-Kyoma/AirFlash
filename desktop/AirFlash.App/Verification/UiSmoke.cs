using System.Globalization;
using System.Net;
using System.Net.Http;
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
// Opt-in, isolated UI verification with fake DNS, engine, endpoint and autostart boundaries.
internal static class UiSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        var report = Program.StartupValue(args, "--output", "--self-check-output") is { } output
            ? Path.GetFullPath(output) : Path.Combine(Path.GetTempPath(), "airplay-ui-smoke", "report.json");
        var directory = Path.GetDirectoryName(report)!; Directory.CreateDirectory(directory);
        AppPaths.DataDirectory = Path.Combine(directory, "isolated-data");
        var options = Program.StartupOptions(args).ToArray();
        var checks = new List<string>();
        Check(!Program.NeedsDiagnosticIsolation([]) && !Program.NeedsDiagnosticIsolation(["--startup"]),
            "ordinary startup arguments preserve the application's normal data directory", checks);
        Check(!Program.NeedsDiagnosticIsolation(["--other-check"])
            && !Program.NeedsDiagnosticIsolation(["--output", "synthetic-output-check"]),
            "unknown check switches and check-suffixed option values do not redirect normal startup", checks);
        Check(new[] { "--discovery-perf", "--update-check", "--self-check", "--stability-check", "--compat-check", "--discovery-check", "--audio-check",
            "--ui-smoke", "--tray-smoke", "--ui-perf" }.All(mode => Program.NeedsDiagnosticIsolation([mode])),
            "every dispatched diagnostic mode selects isolated startup data without invoking its real service", checks);
        Check(string.Equals(Program.DiagnosticStartupDataDirectory, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase),
            "startup diagnostic data uses the smoke output directory before the harness starts", checks);
        var startupLogs = Directory.GetFiles(Path.Combine(AppPaths.DataDirectory, "logs"), "wpf-*.log");
        var startupText = string.Join("\n", startupLogs.Select(File.ReadAllText));
        Check(startupText.Contains("Startup stage=platform", StringComparison.Ordinal)
            && startupText.Contains("Startup stage=wpf-resources ready", StringComparison.Ordinal),
            "both startup stages are recorded in isolated smoke logs", checks);
        Check(!Metadata.SmtcNowPlayingSource.HasMetadata(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
            && !Metadata.SmtcNowPlayingSource.HasMetadata(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            && Metadata.SmtcNowPlayingSource.HasMetadata(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            && Metadata.SmtcNowPlayingSource.HasMetadata(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing),
            "stopped and closed media clear the source while paused and playing media keep metadata", checks);
        foreach (var arguments in new string[][]
        {
            ["--ui-pairing-smoke"], ["--ui-pairing-smoek"], ["--receiver-settings-regression"], ["--ui-sound-glow-smoke"],
            ["--ui-smoke", "--ui-perf"], ["--ui-smoke", "--ui-smoke"], ["--ui-smoke", "--audio-check"],
            ["--ui-smoke", "--discovery-check"], ["--ui-smoke", "--dns-resolve-helper"],
            ["--ui-perf", "--ui-pairing-smoke"], ["--ui-perf", "--ui-unknown"], ["--ui-perf", "--receiver-settings-regression"],
            ["--tray-smoke", "--ui-password-smoke"], ["--tray-smoke", "--ui-session-lifecycle-smoke"],
            ["--tray-smoke", "--receiver-settings-regression"], ["--tray-smoke", "--ui-pairing-clipboard"], ["--tray-smoke", "--ui-unknown"],
            ["--ui-smoke", "--ui-ownership-smoke", "--ui-pairing-hint-smoke"], ["--ui-smoke", "--ui-scroll-hold"],
            ["--ui-smoke", "--ui-diagnostics-dpi"], ["--ui-smoke", "--ui-culture"]
        })
        {
            Check(Program.NeedsDiagnosticIsolation(arguments) && Program.IsDiagnostic(arguments),
                "invalid UI requests retain isolation and a nonmodal failure surface: " + string.Join(" ", arguments), checks);
            var rejected = false;
            try { Program.ValidateUiVerificationArguments(arguments); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "unsafe UI invocation is rejected before platform startup: " + string.Join(" ", arguments), checks);
        }
        foreach (var orphan in new string[][] { ["--output", "--ui-smoke"],
            ["--self-check-output", "--ui-password-smoke"], ["--output", "--ui-smoke", "--audio-check"],
            ["--output", "--self-check"], ["--startup", "--dns-resolve-helper"], ["--startup", "--dns-resolve-helper-check"] })
            Check(Program.HasOrphanedDiagnosticOption(orphan) && Program.NeedsDiagnosticIsolation(orphan),
                "a misplaced diagnostic switch selects an isolated failure before dispatch", checks);
        Check(!Program.IsValidDnsHelperInvocation(["--dns-resolve-helper", "0", "_airplay._tcp.local", "--ui-smoke"])
            && !Program.IsValidDnsHelperInvocation(["--dns-resolve-helper-check", "--output", "--ui-smoke"])
            && Program.IsValidDnsHelperInvocation(["--dns-resolve-helper", "0", "_airplay._tcp.local", "--ui-example._airplay._tcp.local"])
            && Program.IsValidDnsHelperInvocation(["--dns-resolve-helper-check", "--output", "fixture.json"]),
            "helper entry requires a qualified name or isolated report path before dispatch", checks);
        Check(!Program.StartupOptions(["--ui-smoke", "--ui-language", "--audio-check"]).Contains("--audio-check"),
            "a language value cannot select the real audio diagnostic before UI verification", checks);
        Check(Program.StartupValue(["--ui-smoke", "--ui-language", "--output", "--output", "fixture.json"], "--output") == "fixture.json",
            "option lookup skips earlier values spelled like option names", checks);
        Program.ValidateUiVerificationArguments(["--tray-smoke", "--ui-language", "en", "--ui-culture", "en-US",
            "--tray-guid", Guid.NewGuid().ToString(), "--tray-window-id"]);
        checks.Add("tray verification permits culture, language and isolated identity options");
        string[] trayOutput = ["--tray-smoke", "--output", "--ui-password-smoke"];
        Program.ValidateUiVerificationArguments(trayOutput);
        Check(!Program.HasOrphanedDiagnosticOption(trayOutput) && Program.FocusedUiMode(Program.StartupOptions(trayOutput)) is null,
            "an explicit tray route accepts a switch-like output path without selecting a focused fixture", checks);

        var verificationMode = Program.FocusedUiMode(options) ?? (options.Contains("--tray-smoke") ? "tray" : "full");
        void FocusResult(bool ok, Exception? error = null) => App.WriteOutput(args, new
        {
            ok, mode = verificationMode, checks, culture = CultureInfo.CurrentCulture.Name,
            ui_culture = CultureInfo.CurrentUICulture.Name, error = error?.ToString(),
            note = "Isolated WPF verification with synthetic services."
        });
        try
        {
            var focused = verificationMode switch
            {
                "--ui-discovery-smoke" => UiDiscoveryLifecycle.RunAsync(checks),
                "--ui-session-lifecycle-smoke" => UiSessionLifecycle.RunAsync(checks, directory),
                "--ui-diagnostics-smoke" => UiDiagnostics.RunAsync(checks, directory, options.Contains("--ui-diagnostics-dpi")),
                "--ui-regression" or "--ui-regression-smoke" => UiRegression.RunAsync(checks, directory),
                "--receiver-settings-regression" => ReceiverSettingsRegression.RunAsync(checks, directory),
                "--ui-pairing-hint-smoke" => PairingHintRegression.RunAsync(checks, directory),
                "--ui-sound-glow-smoke" => SoundGlowRegression.RunAsync(checks, directory),
                "--ui-ownership-smoke" => UiOwnershipRecovery.RunAsync(checks),
                "--ui-mute-recovery-smoke" => UiMuteRecovery.RunAsync(checks, directory),
                "--ui-settings-recovery-smoke" => UiSettingsRecovery.RunAsync(checks, directory),
                "--ui-pairing-smoke" => UiPairing.RunAsync(checks, directory, options.Contains("--ui-pairing-clipboard")),
                "--ui-password-smoke" => UiPairing.RunPasswordAsync(checks, directory),
                "--ui-scroll-smoke" => UiScrolling.RunAsync(checks, directory, options.Contains("--ui-scroll-hold")),
                "full" or "tray" => null,
                _ => throw new InvalidOperationException("Unmapped focused UI verification mode.")
            };
            if (focused is not null)
            {
                await focused;
                FocusResult(true); return 0;
            }
        }
        catch (Exception error) { FocusResult(false, error); return 1; }
        object? iconEnvironment = null;
        var pageEndLayouts = new List<object>();
        var store = new MemoryStore(); var engine = new MockFactory();
        using var updateClient = new HttpClient(new UpdateHandler());
        var updates = new UpdateCheckState(new UpdateService(updateClient), Version.TryParse(AppPaths.Version, out var currentVersion) ? currentVersion : new Version(0, 0, 0));
        var discovery = new MockDiscovery(); var audio = new MockAudio();
        discovery.Items = discovery.Items.Select(row => row with
        {
            DeviceId = row.Id,
            Members = row.Members.Select(member => member with { DeviceId = member.Id }).ToArray()
        }).ToArray();
        await using var app = new AppViewModel(store, discovery, new MockAutostart(), audio, engine, Application.Current.Dispatcher, updates: updates, isolatedVerification: true) { EngineVersion = "0.1.0（模拟）" };
        var panel = new ControlPanel(app); SettingsWindow? settings = null;
        // Verification must never bind the production GUID to a test executable path.
        var trayGuid = Program.StartupValue(args, "--tray-guid") is { } guid ? Guid.Parse(guid) : Guid.NewGuid();
        if (trayGuid == new Guid("f3f63f27-a6df-4a27-a942-b448e17dcd12")) throw new ArgumentException("Use an isolated verification GUID.");
        var menuOpened = false; var quitRequestedFromTray = false;
        var windowIdentityForVerification = options.Contains("--tray-window-id");
        using var tray = new TrayService(panel, app, () => menuOpened = true, () => quitRequestedFromTray = true, trayGuid, windowIdentityForVerification); panel.Tray = tray;
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
            await Until(() => tray.IsRegistered && tray.TryGetRectangle(out _));
            Check(tray.TryGetRectangle(out var iconBounds) && iconBounds.Right > iconBounds.Left && iconBounds.Bottom > iconBounds.Top, "real tray registration has an icon rectangle", checks);
            Check(app.Receivers.Count == 2 && app.Receivers.All(r => r.Receiver.Online), "offline receivers excluded", checks);
            var area = tray.WorkArea(); GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out var bounds);
            Check(Math.Abs(bounds.Right - (area.Rect.Right - Math.Round(12 * area.Scale))) <= 2 && Math.Abs(bounds.Bottom - (area.Rect.Bottom - Math.Round(12 * area.Scale))) <= 2, "native panel aligns to monitor work area", checks);
            var clickParam = tray.UsesVersion4 ? new IntPtr(0x10400) : new IntPtr(0x202);
            var clickId = tray.UsesVersion4 ? IntPtr.Zero : new IntPtr(1);
            SendMessage(tray.WindowHandle, 0x8001, clickId, clickParam); await Pump();
            Check(!panel.IsVisible, "tray activation hides panel", checks);
            await Task.Delay(280); SendMessage(tray.WindowHandle, 0x8001, clickId, clickParam); await Pump();
            Check(panel.IsVisible, "tray activation opens panel", checks);
            if (options.Contains("--tray-smoke"))
            {
                var initialIdentity = tray.Identity.ToString();
                await VerifyTrayLifecycleAsync(tray, app, trayGuid, () => menuOpened, () => quitRequestedFromTray, checks, windowIdentityForVerification);
                App.WriteOutput(args, new { ok = true, mode = verificationMode, checks, culture = CultureInfo.CurrentCulture.Name, ui_culture = CultureInfo.CurrentUICulture.Name, tray_guid = trayGuid, identity = initialIdentity, window_identity_for_verification = windowIdentityForVerification, path = Environment.ProcessPath, note = "Isolated tray verification; engine/audio/discovery/autostart services are simulated." });
                return 0;
            }
            var mode = Descendants(panel).OfType<ComboBox>().Single(); mode.IsDropDownOpen = true; await Pump();
            Check(panel.IsVisible, "dropdown does not dismiss panel", checks); mode.IsDropDownOpen = false; await Pump();
            var customLatency = ((IEnumerable<StreamChoice>)mode.ItemsSource).Single(c => Equals(c.Value, "custom"));
            Check(customLatency.Label == L.Format("Custom · {0} ms", app.Settings.CustomBufferMs), "global latency menu shows the configured custom delay", checks);
            Check(((IEnumerable<StreamChoice>)mode.ItemsSource).All(c => !Equals(c.Value, "adaptive")), "global latency menu omits the retired adaptive mode", checks);
            CheckPanelIcons(panel, app, checks);
            Check(Descendants(panel).OfType<Button>().All(b => b.Content is not string text || text != L.Get("Sound Glow experiment")), "control panel no longer duplicates the Sound Glow experiment", checks);
            var updateButton = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Update"));
            Check(updateButton.Visibility != Visibility.Visible, "update entry remains hidden until a newer release is found", checks);
            await app.CheckUpdatesAtStartupAsync(); await Pump(); panel.UpdateLayout();
            Check(updateButton.Visibility == Visibility.Visible && updateButton.IsVisible, "startup update check surfaces the localized update entry", checks);
            Check(Descendants(updateButton).OfType<TextBlock>().Any(text => text.Text == L.Get("Update")), "update entry shows its localized label", checks);
            var updateRequested = false;
            void OnUpdateRequested() => updateRequested = true;
            panel.UpdateRequested += OnUpdateRequested;
            Invoke(updateButton); await Pump();
            panel.UpdateRequested -= OnUpdateRequested;
            Check(updateRequested, "update entry requests the About page", checks);
            Render(panel, Path.Combine(directory, "panel-icons-idle.png"), 1);
            var settingsButton = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Settings"));
            Check(settingsButton.Focus() && settingsButton.IsKeyboardFocused, "vector settings button remains keyboard focusable", checks);
            var settingsRequested = false;
            void OnSettings() => settingsRequested = true;
            panel.SettingsRequested += OnSettings;
            Invoke(settingsButton); await Pump();
            panel.SettingsRequested -= OnSettings;
            Check(settingsRequested && (string?)settingsButton.ToolTip == L.Get("Settings"), "vector settings button preserves its action and localized label", checks);
            var latencySettingsButton = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Latency settings"));
            var latencyPage = -1;
            void OnLatencySettings(int page) => latencyPage = page;
            panel.LatencySettingsRequested += OnLatencySettings;
            Check(latencySettingsButton.Focus() && latencySettingsButton.IsKeyboardFocused, "latency settings button remains keyboard focusable", checks);
            Invoke(latencySettingsButton); await Pump();
            panel.LatencySettingsRequested -= OnLatencySettings;
            Check(latencyPage == SettingsViewModel.StreamingPage && (string?)latencySettingsButton.ToolTip == L.Get("Latency settings"), "latency settings button opens the global latency page", checks);
            Check(app.Receivers.All(r => r.VolumeText == "—" && !r.CanSetVolume), "disconnected device volume is unknown and disabled", checks);
            var playbackRow = app.Receivers[0];
            var playbackButton = Descendants(panel).OfType<Button>().Single(button =>
                ReferenceEquals(button.DataContext, playbackRow) && ReferenceEquals(button.Command, playbackRow.PlaybackCommand));
            Check(playbackButton.Focus() && playbackButton.IsKeyboardFocused, "vector playback button remains keyboard focusable", checks);
            Invoke(playbackButton);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            CheckPanelIcons(panel, app, checks);
            Check(engine.Commands.Any(c => c.Text("command") == "start"), "panel start reaches engine", checks);
            await VerifyNoticeDismissalAsync(panel, app, discovery, engine, audio, checks);
            await VerifyDiscoveryRetryPresentationAsync(panel, app, discovery, checks);
            await VerifyDiscoveryNoticeRecoveryAsync(panel, app, discovery, checks);
            await VerifyDiscoveryExportAsync(app, discovery, engine, checks);
            await VerifyConnectingDiscoveryNoticeAsync(checks);
            Invoke(playbackButton);
            await Until(() => app.Snapshot.State == PlaybackState.Idle);
            CheckPanelIcons(panel, app, checks);
            Invoke(playbackButton);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            CheckPanelIcons(panel, app, checks);
            checks.Add("vector playback button completes play/stop/play with localized accessible labels");
            Check(!app.HasNotice && app.Notice.Length == 0 && !app.DismissNoticeCommand.CanExecute(null),
                "a new explicit start clears the previous dismissed notification", checks);
            await Until(() => app.Receivers[0].Volume == 28 && app.Receivers[0].CanSetVolume);
            Check(!engine.Commands.Any(c => c.Text("command") == "set_device_volume"), "initial volume comes from receiver without a write", checks);
            var volumeRow = app.Receivers[0];
            volumeRow.Volume = 44;
            await Until(() => volumeRow.VolumeSyncing);
            await Until(() => volumeRow.Volume == 44 && !volumeRow.VolumeSyncing && volumeRow.VolumeStatus.Length == 0);
            Check(volumeRow.CanSetVolume && app.MasterVolume == 100, "delayed receiver confirmation preserves independent master volume", checks);
            await app.ToggleReceiverMuteAsync(volumeRow.Receiver.Id);
            await Until(() => volumeRow.Muted);
            await app.ToggleReceiverMuteAsync(volumeRow.Receiver.Id);
            await Until(() => volumeRow.Volume == 44 && !volumeRow.Muted);
            Check(volumeRow.Volume == 44, "device unmute restores confirmed value", checks);
            app.MasterVolume = 25; await app.FlushVolumeAsync();
            Check(engine.Commands.Any(c => c.Text("command") == "set_gain" && c.GetProperty("params").Number("gain") == .25), "master gain reaches engine", checks);
            settings = new(app); settings.Show(); await Pump();
            iconEnvironment = IconVerification.Run(settings, tray, checks, directory);
            foreach (var message in new[] { 0x001a, 0x007e })
            {
                SendMessage(tray.WindowHandle, (uint)message, IntPtr.Zero, IntPtr.Zero); await Pump();
            }
            GetWindowRect(tray.WindowHandle, out var trayBounds);
            var rectanglePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeWindowPlacement.Rectangle>());
            try
            {
                Marshal.StructureToPtr(trayBounds, rectanglePointer, false);
                var trayDpi = AppIconService.WindowDpi(tray.WindowHandle);
                SendMessage(tray.WindowHandle, 0x02e0, new((long)(trayDpi | trayDpi << 16)), rectanglePointer);
                await Pump();
            }
            finally { Marshal.FreeHGlobal(rectanglePointer); }
            Check(tray.IconHandle != IntPtr.Zero, "display/settings/DPI notifications preserve tray icon", checks);
            tray.RemoveForVerification();
            SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
            Check(tray.IsRegisteredForVerification, "Explorer recreation registers tray icon again", checks);
            SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
            Check(tray.IsRegisteredForVerification, "repeated Explorer notification retains one registered tray icon", checks);
            Check(!settings.ApplyButton.IsEnabled, "apply initially disabled", checks);
            settings.ViewModel.SelectedPage = SettingsViewModel.AudioCapturePage; await Pump();
            var radio = Descendants(settings).OfType<RadioButton>().Single(r => (string?)r.Content == L.Get("Specific audio endpoint"));
            var radioPeer = UIElementAutomationPeer.CreatePeerForElement(radio)!;
            ((ISelectionItemProvider)radioPeer.GetPattern(PatternInterface.SelectionItem)!).Select();
            await Pump();
            var endpoints = Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "EndpointCombo");
            Check(endpoints.IsEnabled, "capture mode enables endpoint", checks);
            var inputEndpoint = ((IEnumerable<AudioEndpoint>)endpoints.ItemsSource).Single(e => e.Kind == AudioEndpointKind.Recording);
            Check(inputEndpoint.DisplayName.Contains(L.Get("Recording"), StringComparison.Ordinal), "recording endpoint is labeled", checks);
            endpoints.SelectedValue = inputEndpoint.Id; await Pump();
            settings.ViewModel.Draft.CustomBufferMs = 777;
            var startsBefore = engine.Commands.Count(c => c.Text("command") == "start");
            Invoke(settings.ApplyButton); await Until(() => app.Settings.CaptureMode == "endpoint" && !settings.ViewModel.HasChanges);
            Check(customLatency.Label == L.Format("Custom · {0} ms", 777), "custom latency menu follows applied settings", checks);
            await Until(() => engine.Commands.Count(c => c.Text("command") == "start") > startsBefore);
            Check(engine.Commands.Count(c => c.Text("command") == "start") == startsBefore + 1, "settings restart once", checks);
            Check(app.Settings.CaptureEndpoint == inputEndpoint.Id && store.Saved.CaptureEndpoint == inputEndpoint.Id, "recording endpoint persists by ID", checks);
            Check(engine.Commands.Last(c => c.Text("command") == "start").GetProperty("params").GetProperty("capture_endpoint").GetString() == inputEndpoint.Id, "recording endpoint reaches native command", checks);
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
            settings.ViewModel.SelectedPage = SettingsViewModel.AudioCapturePage; await Pump(); settings.ViewModel.SelectedPage = 0; await Pump();
            Check(retries.Text == "invalid", "invalid edits survive page switches", checks);
            Check(!settings.ApplyButton.IsEnabled && !settings.OkButton.IsEnabled, "invalid number blocks save", checks);
            retries.Text = "5"; await Pump();
            settings.ViewModel.AddManual("书房", "127.0.0.2", 7000); await settings.ViewModel.ApplyAsync();
            Check(app.Settings.ManualReceivers.Count == 1, "manual receiver persists", checks);
            var firstId = app.Snapshot.Receiver?.Id ?? throw new InvalidOperationException("The mock receiver must still be streaming.");
            settings.ViewModel.Draft.Options(firstId).Hidden = true; await settings.ViewModel.ApplyAsync();
            Check(app.Receivers.Any(r => r.Receiver.Id == firstId && r.Active && ReferenceEquals(r.PlaybackCommand, app.StopCommand) && r.PlaybackCommand.CanExecute(null))
                && settings.ViewModel.Receivers.Any(r => r.Receiver.Id == firstId), "hidden active receiver retains its Stop control", checks);
            settings.ViewModel.Draft.Options(firstId).Hidden = false; await settings.ViewModel.ApplyAsync();
            Check(app.Receivers.Any(r => r.Receiver.Id == firstId), "unhide receiver", checks);
            settings.Close(); settings = new(app); settings.Show(); await Pump();
            Check(settings.ViewModel.Receivers.Count >= 3, "nullable overrides reopen", checks);
            settings.ViewModel.SelectedPage = SettingsViewModel.AboutPage; await Pump();
            Check(settings.ViewModel.Pages[SettingsViewModel.AboutPage] == L.Get("About")
                && Descendants(settings).OfType<Button>().Any(b => (string?)b.Content == L.Get("Check for updates")),
                "About page exposes manual update actions", checks);
            Check(Descendants(settings).OfType<Button>().Any(b => (string?)b.Content == L.Get("Open download page") && b.IsVisible),
                "About page shares the startup update result", checks);
            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                CheckThemeColors(dark, checks);
                for (var page = 0; page < settings!.ViewModel.Pages.Length; page++)
                {
                    settings.ViewModel.SelectedPage = page; await Pump();
                    Render(settings, Path.Combine(directory, $"settings-{(dark ? "dark" : "light")}-{page}.png"), 1);
                    if (page == 0)
                    {
                        await VerifyPageEndAsync(settings, dark, checks, pageEndLayouts, directory);
                        Render(settings, Path.Combine(directory, $"settings-reset-{(dark ? "dark" : "light")}.png"), 1);
                        settings.PageScroll.ScrollToHome();
                    }
                    if (page == SettingsViewModel.MonitorPage)
                    {
                        await VerifyPageEndAsync(settings, dark, checks, pageEndLayouts, directory);
                        Render(settings, Path.Combine(directory, $"monitor-details-{(dark ? "dark" : "light")}.png"), 1);
                        settings.PageScroll.ScrollToHome();
                    }
                }
                settings.Hide(); panel.ShowPanel(); await Pump();
                CheckPanelIcons(panel, app, checks);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d }) Render(panel, Path.Combine(directory, $"panel-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
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
            checks.Add("nine pages rendered in light/dark; panel at 100/125/150/175/200 percent");
            await app.StopAsync(); await Until(() => app.Snapshot.State == PlaybackState.Idle);
            settings.Hide(); panel.ShowPanel(); await Pump();
            CheckPanelIcons(panel, app, checks);
            Render(panel, Path.Combine(directory, "panel-icons-stopped.png"), 1);
            Check(engine.DisposedCount == engine.CreatedCount, "stop releases mock engine", checks);
            await UiRegression.RunAsync(checks, directory);
            await UiSettingsRecovery.RunAsync(checks, directory);
            await UiMuteRecovery.RunAsync(checks, directory);
            await UiPairing.RunAsync(checks, directory);
            await UiEqualizer.RunAsync(checks, directory);
            await VerifyStartupFailureAsync(checks, directory);
            await UiDiscoveryLifecycle.RunAsync(checks);
            await UiDiagnostics.RunAsync(checks, directory);
            await UiSessionLifecycle.RunAsync(checks, directory);
            await UiScrolling.RunAsync(checks, directory);
            await VerifyTrayLifecycleAsync(tray, app, trayGuid, () => menuOpened, () => quitRequestedFromTray, checks);
            App.WriteOutput(args, new { ok = true, mode = verificationMode, checks, culture = CultureInfo.CurrentCulture.Name,
                ui_culture = CultureInfo.CurrentUICulture.Name, icon_environment = iconEnvironment, note = "DNS, engine, audio and autostart boundaries are simulated. DPI renders do not replace physical multimonitor QA." });
            return 0;
        }
        catch (Exception error) { FocusResult(false, error); return 1; }
        finally { settings?.Close(); panel.ShutdownPanel(); }
    }
    private static async Task VerifyNoticeDismissalAsync(ControlPanel panel, AppViewModel app, MockDiscovery discovery, MockFactory factory, MockAudio audio, List<string> checks)
    {
        var banner = Descendants(panel).OfType<Border>().Single(b => b.Name == "NoticeBanner");
        var dismiss = Descendants(panel).OfType<Button>().Single(b => b.Name == "DismissNoticeButton");
        Check(!banner.IsVisible && !app.DismissNoticeCommand.CanExecute(null), "empty notification has no banner or enabled dismiss command", checks);
        discovery.PublishFailure(" \t "); await Pump();
        Check(!banner.IsVisible && !dismiss.IsEnabled && app.ErrorDetails.Length == 0, "whitespace notification stays hidden", checks);

        const string failure = "Synthetic receiver resolution timeout. Discovery will retry.";
        await app.FlushVolumeAsync();
        var commands = factory.Commands.Count;
        var restores = audio.Restores;
        var owner = app.Session.Capture().Owner;
        discovery.PublishFailure(failure); await Pump();
        Check(app.HasNotice && banner.IsVisible && app.Snapshot.State == PlaybackState.Streaming
            && factory.Commands.Count == commands && audio.Restores == restores && app.Session.Capture().Owner == owner,
            "discovery warning is visible without touching active playback", checks);
        Check(ReferenceEquals(dismiss.Command, app.DismissNoticeCommand) && dismiss.IsEnabled
            && System.Windows.Automation.AutomationProperties.GetName(dismiss) == L.Get("Dismiss notification")
            && (string?)dismiss.ToolTip == L.Get("Dismiss notification") && dismiss.Focus() && dismiss.IsKeyboardFocused,
            "notification dismissal is localized and keyboard accessible", checks);
        Invoke(dismiss); await Pump();
        var details = Descendants(panel).OfType<Expander>().Single(e => Equals(e.Header, L.Get("View error details")));
        Check(!app.HasNotice && !banner.IsVisible && app.Notice == failure && app.ErrorDetails == failure
            && details.IsVisible && factory.Commands.Count == commands && audio.Restores == restores
            && app.Session.Capture().Owner == owner && app.Snapshot.State == PlaybackState.Streaming,
            "dismiss hides the banner and preserves diagnostics and playback", checks);
        discovery.PublishFailure(failure); await Pump();
        Check(!app.HasNotice && !banner.IsVisible, "an identical discovery retry stays dismissed", checks);
        discovery.PublishFailure("Synthetic different discovery warning."); await Pump();
        Check(app.HasNotice && banner.IsVisible && dismiss.IsEnabled && app.ErrorDetails == app.Notice,
            "a different notification becomes visible after dismissal", checks);
        Invoke(dismiss); await Pump();
    }
    private static async Task VerifyDiscoveryRetryPresentationAsync(ControlPanel panel, AppViewModel app, MockDiscovery discovery, List<string> checks)
    {
        var banner = Descendants(panel).OfType<Border>().Single(border => border.Name == "NoticeBanner");
        var dismiss = Descendants(panel).OfType<Button>().Single(button => button.Name == "DismissNoticeButton");
        var active = app.Snapshot.Receiver!;
        var activePeer = active.Peers.Last();
        var other = discovery.Items.Single(receiver => receiver.Online && receiver.Id != active.Id).Peers.First();
        DiscoveryResolveContext Context(Receiver peer) => new(peer.DeviceId, peer.Address, peer.Port, peer.StereoId);
        var priorNotice = app.Notice;
        var priorDetails = app.ErrorDetails;

        discovery.PublishFailure(new DiscoveryFailure("Synthetic other receiver timeout", DiscoveryFailureReason.ResolveTimeout, Context(other)));
        await Pump();
        Check(!banner.IsVisible && app.Notice == priorNotice && app.ErrorDetails == priorDetails,
            "attributed background resolve retry preserves the previous notice during playback", checks);

        const string ambiguous = "Synthetic ambiguous receiver timeout";
        discovery.PublishFailure(new DiscoveryFailure(ambiguous, DiscoveryFailureReason.ResolveTimeout, Context(other) with { DeviceId = "unknown" }));
        await Pump();
        Check(banner.IsVisible && app.Notice == ambiguous, "ambiguous resolve retry remains visible during playback", checks);

        const string relevant = "Synthetic active member resolve failure";
        discovery.PublishFailure(new DiscoveryFailure(relevant, DiscoveryFailureReason.ResolveFailed, Context(activePeer)));
        await Pump();
        Check(banner.IsVisible && app.Notice == relevant, "active receiver or member resolve retry remains visible during playback", checks);

        const string actionable = "Synthetic unavailable adapter warning";
        discovery.PublishFailure(new DiscoveryFailure(actionable, DiscoveryFailureReason.Other, Context(other)));
        await Pump();
        Check(banner.IsVisible && app.Notice == actionable && app.Snapshot.State == PlaybackState.Streaming,
            "actionable discovery warning remains visible during playback", checks);
        Invoke(dismiss); await Pump();
    }

    private static async Task VerifyDiscoveryNoticeRecoveryAsync(ControlPanel panel, AppViewModel app, MockDiscovery discovery, List<string> checks)
    {
        var banner = Descendants(panel).OfType<Border>().Single(border => border.Name == "NoticeBanner");
        var attempt = new DiscoveryResolveAttempt("synthetic._raop._tcp.local", "_raop._tcp.local", 1, 10);
        const string timeout = "Synthetic initial discovery timeout";
        discovery.PublishFailure(new DiscoveryFailure(timeout, DiscoveryFailureReason.ResolveTimeout) { Attempt = attempt });
        await Pump();
        Check(banner.IsVisible && app.Notice == timeout, "unresolved initial discovery timeout remains visible", checks);

        discovery.PublishResolution(attempt with { Epoch = 0, Sequence = 11 });
        discovery.PublishResolution(attempt with { Sequence = 9 });
        discovery.PublishResolution(attempt with { ServiceType = "_airplay._tcp.local", Sequence = 11 });
        await Pump();
        Check(banner.IsVisible && app.Notice == timeout, "stale and different-service resolutions preserve the discovery warning", checks);

        discovery.PublishResolution(attempt with { Instance = "SYNTHETIC._RAOP._TCP.LOCAL.", Epoch = 2, Sequence = 12 });
        await Pump();
        Check(!banner.IsVisible && app.Notice.Length == 0 && app.ErrorDetails.Length == 0,
            "exact service recovery after browse restart clears its old discovery warning", checks);

        discovery.PublishFailure(new DiscoveryFailure(timeout, DiscoveryFailureReason.ResolveTimeout) { Attempt = attempt });
        await Pump();
        app.ShowError(new IOException(timeout));
        discovery.PublishResolution(attempt with { Sequence = 13 });
        await Pump();
        Check(banner.IsVisible && app.Notice == timeout, "discovery recovery preserves a newer actionable notice with identical text", checks);
        app.DismissNoticeCommand.Execute(null);
        await Pump();
    }

    private static async Task VerifyDiscoveryExportAsync(AppViewModel app, MockDiscovery discovery, MockFactory engine, List<string> checks)
    {
        app.SetPanelVisible(true); app.SetReceiversPageVisible(false);
        var starts = discovery.Starts;
        app.SetReceiversPageVisible(true);
        var demand = discovery.SnapshotDiagnostics().Demand;
        Check((demand & (DiscoveryDemand.Panel | DiscoveryDemand.Receivers)) == (DiscoveryDemand.Panel | DiscoveryDemand.Receivers)
            && discovery.Starts == starts, "discovery diagnostics observe changed demand without restarting an already requested browse", checks);
        app.SetReceiversPageVisible(false);
        const string name = "NeverExportFixture._airplay._tcp.local", address = "192.0.2.211";
        discovery.RecordResolutionForDiagnostics(new(name, "_airplay._tcp.local", address, 7000,
            new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:77", ["tsid"] = "NeverExportStereo" }));
        var path = await app.ExportDiagnosticsAsync();
        using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var summary = bundle.RootElement.GetProperty("session");
        var request = engine.Commands.Last(command => command.Text("command") == "diag_summary");
        Check(summary.Text("id") == request.Text("id") && request.Text("id").Length > 0,
            "saved diagnostics contain the reply to the export request sent by the application", checks);
        Check(summary.GetProperty("session").Integer("session_epoch") == 1,
            "saved diagnostics belong to the active native playback epoch", checks);
        var history = bundle.RootElement.GetProperty("discovery").GetProperty("history");
        Check(history.EnumerateArray().Any(entry => entry.Text("event") == "ResolveSucceeded" && entry.Text("service_token").Length == 64),
            "saved diagnostics include the optional discovery provider's history", checks);
        var text = bundle.RootElement.ToString();
        Check(!text.Contains(name, StringComparison.Ordinal) && !text.Contains(address, StringComparison.Ordinal)
            && !text.Contains("NeverExportStereo", StringComparison.Ordinal), "saved discovery diagnostics redact service, endpoint and stereo identities", checks);
    }

    private static async Task VerifyConnectingDiscoveryNoticeAsync(List<string> checks)
    {
        var active = new Receiver("synthetic-active", "Synthetic active", "127.0.0.11") { DeviceId = "001122334455" };
        var other = new Receiver("synthetic-other", "Synthetic other", "127.0.0.12") { DeviceId = "66778899aabb" };
        var discovery = new MockDiscovery { Items = [active, other] };
        var factory = new MockFactory { AutoReady = false };
        await using var app = new AppViewModel(new MemoryStore(), discovery, new MockAutostart(), new MockAudio(), factory,
            Application.Current.Dispatcher, isolatedVerification: true);
        app.SetPanelVisible(true);
        app.Start();
        await app.Startup;
        await Until(() => app.AllReceivers.Count == 2);
        await app.ToggleAsync(active);
        await Until(() => app.Snapshot.State == PlaybackState.Connecting && factory.EmitEvent is not null);
        discovery.PublishFailure(new DiscoveryFailure("Synthetic timeout while connecting", DiscoveryFailureReason.ResolveTimeout,
            new(other.DeviceId, other.Address, other.Port, "")));
        await Pump();
        Check(app.HasNotice, "discovery retry stays visible while a connection is pending", checks);

        factory.EmitEvent!(new { @event = "streaming" });
        await Until(() => app.Snapshot.State == PlaybackState.Streaming && app.Notice.Length == 0);
        Check(!app.HasNotice && factory.Commands.Count(command => command.Text("command") == "start") == 1,
            "successful playback quiets a connecting-time retry for a confirmed different receiver", checks);
    }

    private static async Task VerifyStartupFailureAsync(List<string> checks, string directory)
    {
        var attempts = 0;
        await using var engine = new EngineInitialization(_ => ++attempts == 1
            ? Task.FromException<string>(new UnauthorizedAccessException("simulated cache access denied"))
            : Task.FromResult("0.1.0"));
        var factory = new MockFactory();
        await using var app = new AppViewModel(new MemoryStore(), new MockDiscovery(), new MockAutostart(), new MockAudio(), factory, Application.Current.Dispatcher, engine, isolatedVerification: true);
        var panel = new ControlPanel(app);
        try
        {
            panel.ShowPanel(); app.Start(); await app.Startup; await Pump();
            var receiver = app.Receivers.First(r => r.Receiver.Online && r.Receiver.Complete);
            Check(panel.IsVisible && !receiver.CanPlay, "shell is visible before engine initialization", checks);
            await app.ToggleAsync(receiver.Receiver);
            Check(factory.CreatedCount == 0, "initializing engine rejects playback without spawning a process", checks);
            await engine.InitializeAsync(); await Pump();
            using var settings = new SettingsViewModel(app);
            var row = settings.Receivers.First(r => r.Receiver.Online && r.Receiver.Complete);
            Check(app.EngineFailed && !receiver.ToggleCommand.CanExecute(null) && !row.PairCommand.CanExecute(null), "failed engine disables playback and pairing", checks);
            Check(app.RetryEngineCommand.CanExecute(null) && settings.CancelCommand.CanExecute(null), "failed engine preserves retry and settings", checks);
            using var missingDns = new WindowsDiscovery(dnsAvailable: () => false);
            string? failure = null; missingDns.Failed += value => failure = value; missingDns.Start();
            Check(failure is not null, "missing DNS-SD produces a recoverable diagnostic", checks);
            app.ShowError(new IOException("simulated discovery failure")); await Pump();
            Render(panel, Path.Combine(directory, "startup-failure.png"), 1);
            var messages = Descendants(panel).OfType<TextBlock>().Where(t => t.Text == app.Notice || t.Text == app.EngineMessage).ToArray();
            Check(messages.Length == 2 && messages[0].TranslatePoint(new(0, messages[0].ActualHeight), panel).Y <= messages[1].TranslatePoint(new(0, 0), panel).Y,
                "engine and discovery error banners remain readable without overlap", checks);
            var calls = 0; var dwmErrors = new List<string>();
            foreach (var build in new[] { 17763, 19044, 19045 })
                WindowThemeService.ApplyAttributes(new(new(10, 0, build), Architecture.X64, Architecture.X64, true), ThemeService.CurrentPalette,
                    (_, _) => { calls++; return 0; }, dwmErrors.Add);
            Check(calls == 0, "Windows 10 skips Windows 11 DWM attributes", checks);
            WindowThemeService.ApplyAttributes(new(new(10, 0, 22000), Architecture.X64, Architecture.X64, true), ThemeService.CurrentPalette,
                (_, _) => { calls++; return unchecked((int)0x80070057); }, dwmErrors.Add);
            Check(calls == 3 && dwmErrors.Count == 3 && panel.IsVisible, "failed optional DWM attributes are logged without closing the shell", checks);
            await engine.InitializeAsync(); await Pump();
            Check(app.EngineReady && receiver.ToggleCommand.CanExecute(null) && row.PairCommand.CanExecute(null), "successful retry restores playback and pairing", checks);
        }
        finally { panel.ShutdownPanel(); }
        var store = new MemoryStore(); var preferences = store.Load(); preferences.StartAtLogin = true; store.Save(preferences);
        var startup = new MockAutostart();
        await using var audioFailure = new AppViewModel(store, new MockDiscovery(), startup, new MockAudio { FailDefault = true }, new MockFactory(), Application.Current.Dispatcher, isolatedVerification: true);
        audioFailure.SetPanelVisible(true); audioFailure.Start(); await audioFailure.Startup; await Pump();
        Check(startup.Enabled && audioFailure.Receivers.Count > 0 && audioFailure.Notice.Contains("mock audio failed", StringComparison.Ordinal),
            "audio initialization failure preserves autostart and discovery initialization", checks);
        await using var startupFailure = new AppViewModel(store, new MockDiscovery(), new MockAutostart { FailEnable = true }, new MockAudio(), new MockFactory(), Application.Current.Dispatcher, isolatedVerification: true);
        startupFailure.SetPanelVisible(true); startupFailure.Start(); await startupFailure.Startup; await Pump();
        Check(startupFailure.Receivers.Count > 0 && startupFailure.Notice.Contains("mock startup failed", StringComparison.Ordinal),
            "autostart initialization failure preserves discovery and the shell model", checks);
    }
    private static async Task VerifyTrayLifecycleAsync(TrayService tray, AppViewModel app, Guid guid, Func<bool> settingsRequested, Func<bool> quitRequested, List<string> checks, bool windowIdentityForVerification = false)
    {
        // Simulate a shell rebuild without restarting the user's Explorer process.
        tray.RemoveForVerification();
        SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
        await Until(() => tray.IsRegistered && tray.TryGetRectangle(out _));
        Check(tray.TryGetRectangle(out _), "Explorer recreation restores tray geometry", checks);
        SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
        Check(tray.IsRegistered && tray.TryGetRectangle(out _), "repeated Explorer notification retains registered tray geometry", checks);
        SendMessage(tray.WindowHandle, 0x8001, tray.UsesVersion4 ? IntPtr.Zero : new(1), tray.UsesVersion4 ? new(0x1007b) : new(0x205));
        await Pump();
        var menu = tray.ActiveMenu ?? throw new InvalidOperationException("The tray context menu did not open.");
        var items = menu.Items.OfType<MenuItem>().ToArray();
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(settingsRequested(), "tray menu opens settings", checks);
        items[^1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(quitRequested(), "tray menu requests quit", checks);
        menu.IsOpen = false; await Pump();
        var window = tray.WindowHandle;
        tray.Dispose();
        Check(!tray.IsRegistered && !tray.TryGetRectangle(out _), "disposed tray stops registration", checks);
        Check(!TrayRectangleExists(guid, tray.Identity, window), "disposed tray is removed from shell", checks);
        var originalLanguage = CultureInfo.CurrentUICulture.Name;
        var originalCatalogMarker = L.Get("Settings and devices");
        var originallyChinese = L.IsChinese;
        try
        {
            L.Initialize([originallyChinese ? "en-US" : "zh-CN"]);
            var replacement = new ControlPanel(app);
            using var rebuilt = new TrayService(replacement, app, () => { }, () => { }, guid, windowIdentityForVerification);
            try
            {
                await Until(() => rebuilt.IsRegistered && rebuilt.TryGetRectangle(out _));
                Check(rebuilt.TryGetRectangle(out _), "tray is registered after language window rebuild", checks);
            }
            finally { rebuilt.Dispose(); replacement.ShutdownPanel(); }
        }
        finally { L.Initialize([originalLanguage]); }
        Check(L.Get("Settings and devices") == originalCatalogMarker, "tray language rebuild restores original UI catalog", checks);
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrayIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    private static bool TrayRectangleExists(Guid guid, TrayIdentity identity, IntPtr window)
    {
        var identifier = new TrayIdentifier { Size = (uint)Marshal.SizeOf<TrayIdentifier>(), Window = window, Id = 1, Guid = identity == TrayIdentity.Guid ? guid : Guid.Empty };
        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref TrayIdentifier identifier, out NativeWindowPlacement.Rectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeWindowPlacement.Rectangle rectangle);
    private static void Check(bool value, string message, List<string> checks) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private static void CheckPanelIcons(ControlPanel panel, AppViewModel app, List<string> checks)
    {
        var icons = Descendants(panel).OfType<System.Windows.Shapes.Path>()
            .Where(p => p.Name is "SettingsIcon" or "ReceiverIcon" or "PlaybackIcon").ToArray();
        Check(icons.Count(p => p.Name == "SettingsIcon") == 1
            && icons.Count(p => p.Name == "ReceiverIcon") == app.Receivers.Count
            && icons.Count(p => p.Name == "PlaybackIcon") == app.Receivers.Count,
            "each receiver simultaneously displays its own speaker and playback vectors", checks);
        foreach (var icon in icons)
        {
            var row = icon.DataContext as ReceiverViewModel;
            var key = icon.Name switch
            {
                "SettingsIcon" => "SettingsIconGeometry",
                "ReceiverIcon" => "ReceiverIconGeometry",
                _ => row?.NeedsDisconnect == true ? "StopIconGeometry" : "PlayIconGeometry"
            };
            Check(ReferenceEquals(icon.Data, panel.FindResource(key)) && !icon.Data.IsEmpty(),
                $"{icon.Name} uses its expected built-in {key}", checks);
            var pen = icon.Stroke is null ? null : new Pen(icon.Stroke, icon.StrokeThickness)
            {
                StartLineCap = icon.StrokeStartLineCap, EndLineCap = icon.StrokeEndLineCap, LineJoin = icon.StrokeLineJoin
            };
            var bounds = icon.RenderedGeometry.GetRenderBounds(pen);
            Check(icon.IsVisible && icon.ActualWidth > 0 && icon.ActualHeight > 0 && !bounds.IsEmpty
                && new Rect(0, 0, icon.ActualWidth, icon.ActualHeight).Contains(bounds),
                $"{icon.Name} is visible and fits inside its design canvas", checks);
            var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(icon);
            var pixels = new byte[24 * 24 * 4]; bitmap.CopyPixels(pixels, 24 * 4, 0);
            Check(Enumerable.Range(0, 24 * 24).Any(i => pixels[i * 4 + 3] != 0),
                $"{icon.Name} renders nontransparent pixels without glyph lookup", checks);
            var brush = (icon.Stroke ?? icon.Fill) as SolidColorBrush;
            var expected = ResourceColor(icon.Name == "SettingsIcon" ? "TextBrush" : "AccentBrush");
            Check(brush?.Color == expected, $"{icon.Name} follows the current theme color", checks);
            if (icon.Name == "PlaybackIcon")
            {
                var playbackRow = row ?? throw new InvalidOperationException("The playback icon has no receiver row.");
                DependencyObject? parent = VisualTreeHelper.GetParent(icon);
                while (parent is not null && parent is not Button) parent = VisualTreeHelper.GetParent(parent);
                var button = parent as Button ?? throw new InvalidOperationException("The playback icon is outside its button.");
                var expectedCommand = playbackRow.NeedsDisconnect ? app.StopCommand : playbackRow.ToggleCommand;
                Check(ReferenceEquals(button.Command, expectedCommand)
                    && System.Windows.Automation.AutomationProperties.GetName(button) == playbackRow.PlayHint
                    && (string?)button.ToolTip == playbackRow.PlayHint,
                    "playback vector retains its command, accessible name and tooltip for the current state", checks);
            }
        }
    }
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
    private static async Task VerifyPageEndAsync(SettingsWindow settings, bool dark, List<string> checks, List<object> layouts, string directory)
    {
        // Auto scrollbars need not overflow on a large viewport. Verify access
        // through the actual page viewer for both fitting and overflowing pages.
        var scroll = settings.PageScroll;
        scroll.ScrollToBottom(); await Pump();
        var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter
            ?? throw new InvalidOperationException("The settings page viewport is unavailable.");
        var page = settings.PageContainer.Children.OfType<FrameworkElement>().Single(item => item.IsVisible);
        FrameworkElement target = settings.ViewModel.SelectedPage == 0
            ? Descendants(page).OfType<Button>().Single(button => ReferenceEquals(button.Command, settings.ViewModel.ResetCommand))
            : ((StackPanel)page).Children.OfType<TextBlock>().Last();
        var bounds = target.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(settings);
        var label = $"{(dark ? "dark" : "light")} page {settings.ViewModel.SelectedPage} end";
        layouts.Add(new
        {
            label, window_width = settings.ActualWidth, window_height = settings.ActualHeight,
            dpi_x = dpi.DpiScaleX, dpi_y = dpi.DpiScaleY, extent_height = scroll.ExtentHeight,
            viewport_height = scroll.ViewportHeight, scrollable_height = scroll.ScrollableHeight, offset = scroll.VerticalOffset,
            target_bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
            viewer_width = scroll.ActualWidth, viewer_height = scroll.ActualHeight,
            viewport_width = viewport.ActualWidth, viewport_render_height = viewport.ActualHeight, target.IsVisible
        });
        File.WriteAllText(Path.Combine(directory, "page-end-layout.json"), JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1, label + " reaches the final scroll position", checks);
        Check(target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0 && viewport.ActualWidth > 0 && viewport.ActualHeight > 0
            && bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
            label + " keeps the Reset action or final Monitor explanation inside the viewport", checks);
    }
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
    private sealed class UpdateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v999.0.0","draft":false,"prerelease":false}""")
            });
    }
    internal sealed class MemoryStore : ISettingsStore
    {
        public bool Fail;
        public int DelayMs;
        public int Saves;
        public int Resets;
        public bool FailReset;
        public TaskCompletionSource? SaveGate { get; set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public AppSettings Saved => _settings.Clone();
        private AppSettings _settings = new() { AutoConnectOnDiscover = false, MuteWhileStreaming = false, ShowWiderVolume = false };
        public AppSettings Load() => _settings.Clone();
        public Task ResetAsync() { Resets++; if (FailReset) throw new IOException("mock reset failed"); _settings = new(); return Task.CompletedTask; }
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
        public IReadOnlyList<AudioEndpoint> Items = [new("endpoint-a", "扬声器 · Realtek Audio"), new("endpoint-b", "虚拟声卡 · VB-CABLE"), new("endpoint-c", "Voicemeeter Output", AudioEndpointKind.Recording)];
        public void NotifyEndpoints() => EndpointsChanged?.Invoke();
        public string? DefaultEndpoint { get; set; } = "endpoint-a";
        public bool FailDefault { get; set; }
        public Task<string?> GetDefaultEndpointIdAsync() => FailDefault ? Task.FromException<string?>(new IOException("mock audio failed")) : Task.FromResult(DefaultEndpoint);
        public async Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync()
        {
            UiPerformance.Count("audio.enumerate"); Enumerations++;
            ActiveEnumerations++; MaxEnumerations = Math.Max(MaxEnumerations, ActiveEnumerations);
            var items = Items.ToArray(); EnumerationEntered?.TrySetResult();
            try { if (EnumerationGate is { } gate) await gate.Task; await Task.Delay(DelayMs); return items; }
            finally { ActiveEnumerations--; }
        }
        public bool FailMute { get; set; }
        public int Restores;
        public Task MuteAsync(string? endpointId) => FailMute ? Task.FromException(new IOException("mock mute failed")) : Task.CompletedTask;
        public Task RestoreAsync() { Restores++; return Task.CompletedTask; }
    }
    internal sealed class MockDiscovery : IDiscoveryService, IDiscoveryNotifications, IDiscoveryDiagnostics
    {
        private readonly object _diagnosticLock = new();
        private readonly DiscoveryHistory _history = new();
        private DiscoveryDemand _demand;
        public void RecordDemand(DiscoveryDemand demand)
        {
            lock (_diagnosticLock)
            {
                if (_demand == demand) return;
                _demand = demand;
                _history.Record(DateTimeOffset.UtcNow, TimeSpan.Zero, Starts + Stops, DiscoveryTraceEvent.DemandChanged,
                    DiscoveryTraceReason.None, demand, Items.Count);
            }
        }
        public void RecordResolutionForDiagnostics(ServiceRecord record)
        {
            lock (_diagnosticLock) _history.Record(DateTimeOffset.UtcNow, TimeSpan.Zero, Starts + Stops,
                DiscoveryTraceEvent.ResolveSucceeded, DiscoveryTraceReason.None, _demand, Items.Count, record: record);
        }
        public DiscoveryDiagnosticsSnapshot SnapshotDiagnostics()
        {
            lock (_diagnosticLock) return new(Starts + Stops, _demand, Running, Running, true, Items.Count, Items.Count,
                0, 0, _history.Recorded, _history.Overwritten, _history.Snapshot());
        }
        public event Action<bool>? RunningChanged;
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public event Action<IReadOnlyList<Receiver>>? Changed;
        public event Action<string>? Failed;
        public event Action<DiscoveryFailure>? FailureReported;
        public event Action<DiscoveryResolveAttempt>? ResolveSucceeded;
        public void PublishFailure(string message) => PublishFailure(new DiscoveryFailure(message));
        public void PublishFailure(DiscoveryFailure failure)
        {
            Failed?.Invoke(failure.Message);
            FailureReported?.Invoke(failure);
        }
        public void PublishResolution(DiscoveryResolveAttempt resolution) => ResolveSucceeded?.Invoke(resolution);
        public void Start() { if (Running) return; Running = true; Starts++; RunningChanged?.Invoke(true); Publish(); }
        public void Stop() { if (!Running) return; Running = false; Stops++; RunningChanged?.Invoke(false); }
        public string SelectedInterface { get; private set; } = "";
        public void SetInterface(string id) { SelectedInterface = id; Changed?.Invoke(id.Length == 0 ? Items : []); }
        public IReadOnlyList<Receiver> Items { get; set; } = [
            new("a", "家庭影院", "127.0.0.1") { Model = "HomePod" },
            new("b", "客厅立体声", "127.0.0.2") { StereoId = "pair", Members = [new("left", "左侧", "127.0.0.2"), new("right", "右侧", "127.0.0.3")] },
            new("c", "卧室 HomePod mini", "127.0.0.4") { Online = false, Model = "HomePod mini" }
        ];
        public void Publish() => Changed?.Invoke(Items);
        public void Dispose() => Stop();
    }
    internal sealed class MockFactory : IEngineFactory
    {
        public int StopDelayMs;
        public TaskCompletionSource? StopEntered, StopGate;
        public bool FailOpen;
        public bool AutoReady = true;
        public Action<object>? EmitEvent;
        public ConcurrentQueue<JsonElement> Commands { get; } = new();
        /// <summary>Simulated per-host hardware volume, shared across connections.</summary>
        public ConcurrentDictionary<string, int> DeviceVolumes { get; } = new();
        public bool SupportsSoundGlow { get; init; } = true;
        public int CreatedCount, DisposedCount;
        public IEngineConnection Open()
        {
            UiPerformance.Count("engine.open"); Interlocked.Increment(ref CreatedCount);
            if (FailOpen) throw new IOException("mock connection failed");
            return new MockConnection(this);
        }
    }
    private sealed class MockConnection(MockFactory factory) : IEngineConnection, ICorrelatedEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private CancellationTokenSource? _metrics;
        private string _host = "";
        private readonly Dictionary<string, int> _volumes = [];
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
            => SendCorrelatedAsync(Guid.NewGuid().ToString("N"), session, command, parameters, cancellation);
        public Task SendCorrelatedAsync(string requestId, string session, string command, object? parameters, CancellationToken cancellation)
        {
            factory.Commands.Enqueue(JsonSerializer.SerializeToElement(new { id = requestId, command, @params = parameters }));
            if (command == "hello") Emit(session, new { @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "multi_room", "continuity", "buffered_audio", "member_volume", "initial_volume", "receiver_password", "standby_release", "receiver_ownership_guard", "application_capture", "forced_latency", "now_playing", "diagnostics" }.Concat(factory.SupportsSoundGlow ? ["sound_glow_test"] : []).ToArray() });
            if (command == "pair") factory.EmitEvent = payload => Emit(session, payload);
            if (command == "start")
            {
                var data = JsonSerializer.SerializeToElement(parameters);
                _host = data.GetProperty("peers")[0].Text("host");
                foreach (var peer in data.GetProperty("peers").EnumerateArray())
                {
                    var host = peer.Text("host");
                    var fallback = host == _host ? 28 : 45;
                    var volume = peer.TryGetProperty("volume", out var requested) && requested.ValueKind == JsonValueKind.Number
                        ? (int)requested.GetDouble()
                        : factory.DeviceVolumes.GetValueOrDefault(host, fallback);
                    _volumes[host] = volume;
                    factory.DeviceVolumes[host] = volume;
                    Emit(session, new { @event = "negotiated", receiver_id = peer.Text("receiver_id"), host, port = peer.Integer("port"), codec = "alac", transport = "realtime", timing = "ptp", clock_role = peer.GetProperty("profile").Boolean("follow_receiver_clock") == true ? "follow" : "sender", sample_rate = data.Integer("sample_rate") });
                }
                factory.EmitEvent = payload => Emit(session, payload);
                if (factory.AutoReady) Emit(session, new { @event = "streaming", session_epoch = 1 });
                var hosts = data.Text("group_mode") is "software_stereo" or "multi_room" ? data.GetProperty("peers").EnumerateArray().Select(p => p.Text("host")).ToArray() : [_host];
                foreach (var host in hosts)
                    Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence = 0, status = "confirmed", available = true });
                _metrics = new(); var token = _metrics.Token;
                _ = Task.Run(async () => { try { while (!token.IsCancellationRequested) { Emit(session, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 10.8, max_queue_age_ms = 11.8, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } }); Emit(session, new { @event = "transport_metrics", session_uptime_ms = 123456, sender_late_recoveries = 2, skipped_packets = 10, members = new[] { new { host = "192.0.2.1", media = new { packets_sent = 12000, media_send_errors = 1, sync_send_errors = 0, retransmit_requests = 6, retransmits_sent = 5, retransmit_missing = 0, retransmit_expired = 1, retransmit_queue_drops = 0, retransmit_send_errors = 0, receiver_latency_ms = 150, receiver_latency_estimated = true }, health = new { feedback_rtt_ms = 4.2, feedback_failures = 0, feedback_delayed = false } } } }); await Task.Delay(350, token); } } catch (OperationCanceledException) { } }, CancellationToken.None);
            }
            if (command == "diag_summary")
                Emit(session, new { @event = "diag_summary", id = requestId, session = new { session_epoch = 1, session_uptime_ms = 123456 } });
            if (command == "set_device_volume")
            {
                var data = JsonSerializer.SerializeToElement(parameters);
                var value = (int)data.Number("volume")!; var sequence = data.Integer("sequence");
                var host = data.Text("host"); if (host.Length == 0) host = _host;
                Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence, status = "pending", available = true });
                _ = Task.Run(async () => {
                    await Task.Delay(350); _volumes[host] = value; factory.DeviceVolumes[host] = value;
                    Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence, status = "confirmed", available = true });
                });
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
        public async ValueTask DisposeAsync() { factory.StopEntered?.TrySetResult(); if (factory.StopGate is { } gate) await gate.Task; await Task.Delay(factory.StopDelayMs); if (_metrics is not null) { await _metrics.CancelAsync(); _metrics.Dispose(); } _events.Writer.TryComplete(); Interlocked.Increment(ref factory.DisposedCount); }
    }
}
