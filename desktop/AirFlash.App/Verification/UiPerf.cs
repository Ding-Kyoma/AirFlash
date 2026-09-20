using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class UiPerf
{
    private static string _stage = "startup";
    public static async Task<int> RunAsync(string[] args)
    {
        AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), "airflash-ui-perf", Guid.NewGuid().ToString("N"));
        UiPerformance.Enabled = true;
        var dispatcher = Application.Current.Dispatcher;
        var audio = new UiSmoke.MockAudio { DelayMs = 300 };
        var store = new UiSmoke.MemoryStore { DelayMs = 500 };
        var engine = new UiSmoke.MockFactory { StopDelayMs = 300 };
        await using var app = new AppViewModel(store, new UiSmoke.MockDiscovery(), new UiSmoke.MockAutostart(), audio, engine, dispatcher);
        var panel = new ControlPanel(app);
        SettingsWindow? settings = null;
        var phase = "cold";
        using var cancellation = new CancellationTokenSource();
        var probes = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();
                var sampledPhase = phase;
                var sampledStage = _stage;
                await dispatcher.InvokeAsync(() =>
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    UiPerformance.Record("dispatcher.input", elapsed);
                    UiPerformance.Record($"dispatcher.{sampledPhase}", elapsed);
                    if (elapsed > 100) UiPerformance.Record($"stall.{sampledStage}-to-{_stage}", elapsed);
                }, DispatcherPriority.Input);
                await Task.Delay(10);
            }
        });
        try
        {
            app.Start();
            await Frame();
            await Measure("panel.first", () => panel.ShowPanel());
            panel.Hide();
            await Measure("settings.first", () => { settings = new(app); settings.Show(); });
            for (var page = 0; page < 6; page++)
            {
                var selected = page;
                await Measure($"page.{page}.first", () => settings!.ViewModel.SelectedPage = selected);
            }
            for (var sample = 0; sample < 30; sample++)
            {
                phase = "warm";
                for (var page = 0; page < 6; page++)
                {
                    var selected = page;
                    await Measure("page.cached", () => settings!.ViewModel.SelectedPage = selected);
                }
                settings!.Hide();
                await Measure("panel.warm", panel.ShowPanel);
                panel.Hide(); settings.Show();
            }
            settings!.Close();
            await Measure("settings.reopen", () => { settings = new(app); settings.Show(); });
            for (var i = 0; i < 30; i++)
            {
                _stage = "apply";
                settings!.ViewModel.Draft.ForceReconnect = !settings.ViewModel.Draft.ForceReconnect;
                var start = Stopwatch.GetTimestamp();
                var feedback = dispatcher.InvokeAsync(() => UiPerformance.Record("apply.feedback", Stopwatch.GetElapsedTime(start).TotalMilliseconds), DispatcherPriority.Render);
                var apply = settings.ViewModel.ApplyAsync();
                await feedback;
                if (!await apply) throw new InvalidOperationException(settings.ViewModel.Error);
                UiPerformance.Record("apply.total", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            _stage = "playback.start";
            await app.ToggleAsync(app.AllReceivers.First(r => r.Online));
            await Task.Delay(100);
            settings!.ViewModel.Draft.CaptureMode = "endpoint";
            settings.ViewModel.Draft.CaptureEndpoint = "endpoint-b";
            settings.ViewModel.Draft.LatencyMode = "buffered";
            await MeasureAsync("apply.restart", () => settings.ViewModel.ApplyAsync());
            _stage = "playback.stop";
            await app.StopAsync();
            cancellation.Cancel(); await probes;
            static double P95(string name) { var samples = UiPerformance.ReadSamples(name).Order().ToArray(); return samples[(int)Math.Ceiling(samples.Length * .95) - 1]; }
            var targets = new
            {
                apply_feedback_under_100ms = P95("apply.feedback") < 100,
                cached_pages_under_100ms = P95("page.cached") < 100,
                warm_dispatcher_under_50ms = P95("dispatcher.warm") < 50,
                endpoint_enumerations = UiPerformance.ReadCount("audio.enumerate") == 2
            };
            App.WriteOutput(args, new { ok = true, targets, injected = new { enumerate_ms = 300, save_ms = 500, stop_ms = 300 }, performance = UiPerformance.Report() });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, error = error.ToString(), performance = UiPerformance.Report() }); return 1; }
        finally { cancellation.Cancel(); await probes; settings?.Close(); panel.ShutdownPanel(); }
    }
    private static async Task Frame() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task Measure(string name, Action action)
    {
        _stage = name;
        using (UiPerformance.Measure(name)) { action(); await Frame(); }
    }
    private static async Task MeasureAsync(string name, Func<Task<bool>> action)
    {
        _stage = name;
        using (UiPerformance.Measure(name)) { if (!await action()) throw new InvalidOperationException(name); await Frame(); }
    }
}
