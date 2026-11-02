using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class PairingHintRegression
{
    private const string HintKey = "In the Home app, set speaker access to “Everyone,” then try again.";
    private const string PairingKey = "This device requires a password or pairing that AirFlash could not complete. Pair it again in Receivers.";

    public static async Task RunAsync(List<string> checks, string directory)
    {
        var receiver = new Receiver("hint-manual", "Pairing hint test", "192.0.2.10") { IsManual = true };
        var engine = new MockFactory { AutoReady = false };
        await using var app = new AppViewModel(new MemoryStore(), new MockDiscovery { Items = [receiver] },
            new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher);
        app.SetPanelVisible(true); app.Start(); await app.Startup;
        var panel = new ControlPanel(app) { MenuOpen = true };
        panel.ShowPanel(); await Pump();
        var hint = (TextBlock)panel.FindName("PairingActionHint");
        var details = Descendants(panel).OfType<Expander>().Single();
        try
        {
            foreach (var pairing in new[] { false, true })
            {
                await StartAttempt(pairing);
                Check(app.ActionHint.Length == 0 && hint.Visibility == Visibility.Collapsed,
                    "connecting and pairing begin without a previous hint", checks);
                var notice = new EngineNotice("authentication_failed", receiver.Address, "authentication",
                    "pair setup failed: pairing rejected (TLV error [2]): Caused by: rejected by accessory", false);
                EmitFault(notice);
                await Until(() => app.Snapshot.State == PlaybackState.Error && app.ActionHint.Length > 0);
                Check(app.StatusDetail == L.Get(PairingKey) && app.ActionHint == L.Get(HintKey),
                    "playback and explicit pairing keep the existing error and add localized Home access guidance", checks);
                Check(app.ErrorDetails == notice.Detail && app.Snapshot.Diagnostics?.LastFault == notice,
                    "the error expander keeps the complete original TLV fault", checks);
                Check(!details.IsExpanded && hint.IsVisible && hint.Text == app.ActionHint,
                    "the hint is visible without expanding error details", checks);
                Check(AutomationProperties.GetLiveSetting(hint) == AutomationLiveSetting.Polite
                    && AutomationProperties.GetName(hint) == app.ActionHint,
                    "pairing guidance is a polite live region with localized accessible text", checks);
                Check(app.Diagnostics().Contains(app.ActionHint, StringComparison.Ordinal)
                    && !app.StatusDetail.Contains(app.ActionHint, StringComparison.Ordinal)
                    && !app.MonitorLastFault.Contains(app.ActionHint, StringComparison.Ordinal),
                    "the copied summary includes guidance without adding it to the primary error or monitor history", checks);

                if (!pairing)
                {
                    foreach (var dark in new[] { false, true })
                    {
                        ThemeService.SetForVerification(dark); await Pump();
                        var status = Descendants(panel).OfType<TextBlock>().Single(t => t.Text == app.StatusDetail);
                        var origin = hint.TranslatePoint(new(0, 0), panel);
                        Check(origin.Y >= status.TranslatePoint(new(0, status.ActualHeight), panel).Y
                            && origin.X >= 0 && origin.X + hint.ActualWidth <= panel.ActualWidth + 1
                            && origin.Y + hint.ActualHeight <= panel.ActualHeight + 1,
                            "the wrapped hint sits below the error and fits inside the panel", checks);
                        Check(hint.TextWrapping == TextWrapping.Wrap && hint.TextTrimming == TextTrimming.None
                            && double.IsPositiveInfinity(hint.MaxHeight)
                            && hint.ActualHeight + 1 >= hint.DesiredSize.Height - hint.Margin.Top - hint.Margin.Bottom,
                            "the hint has no height cap, ellipsis or clipped lines", checks);
                        foreach (var scale in new[] { 1d, 2d })
                            Render(panel, Path.Combine(directory, $"pairing-hint-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
                    }
                    details.IsExpanded = true; await Pump();
                    Check(Descendants(details).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == notice.Detail),
                        "expanding error details exposes the raw engine fault", checks);
                    details.IsExpanded = false;
                    var export = await app.ExportDiagnosticsAsync();
                    using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(export));
                    Check(!bundle.RootElement.TryGetProperty("action_hint", out _)
                        && !bundle.RootElement.TryGetProperty("ActionHint", out _)
                        && !bundle.RootElement.ToString().Contains(app.ActionHint, StringComparison.Ordinal),
                        "JSON diagnostic exports keep their existing format without UI guidance", checks);
                    var settings = new SettingsWindow(app); settings.ViewModel.SelectedPage = SettingsViewModel.MonitorPage;
                    settings.Show(); await Pump();
                    try
                    {
                        Check(!Descendants(settings).OfType<TextBlock>().Any(t => t.Text == app.ActionHint),
                            "the settings monitor does not display the action hint", checks);
                    }
                    finally { settings.Close(); panel.ShowPanel(); await Pump(); }
                }

                await app.StopAsync(); await Pump();
                Check(app.ActionHint.Length == 0 && hint.Visibility == Visibility.Collapsed
                    && app.MonitorLastFault == notice.Detail && !app.Diagnostics().Contains(L.Get(HintKey), StringComparison.Ordinal),
                    "stopping removes guidance while retaining the original fault history", checks);
            }

            await StartAttempt(false);
            EmitFault(new("authentication_failed", receiver.Address, "authentication", "pairing rejected (TLV error [3])", false));
            await Until(() => app.Snapshot.State == PlaybackState.Error);
            Check(app.ActionHint.Length == 0 && hint.Visibility == Visibility.Collapsed,
                "another authentication error does not reuse the TLV 2 guidance", checks);
        }
        finally { panel.ShutdownPanel(); }

        async Task StartAttempt(bool pairing)
        {
            engine.EmitEvent = null;
            if (pairing) await app.Session.PairAsync(receiver, app.Settings.Clone(),
                (_, _) => throw new InvalidOperationException("This synthetic rejection must not request a PIN."));
            else await app.ToggleAsync(receiver);
            await Until(() => engine.EmitEvent is not null && app.Snapshot.State is PlaybackState.Connecting or PlaybackState.Pairing);
        }
        void EmitFault(EngineNotice notice) => engine.EmitEvent!(new
        {
            @event = "error", code = notice.Code, host = notice.Host, channel = notice.Channel,
            message = notice.Message, retryable = notice.Retryable
        });
    }

    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(70);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(20, timeout.Token);
        await Pump();
    }
}
