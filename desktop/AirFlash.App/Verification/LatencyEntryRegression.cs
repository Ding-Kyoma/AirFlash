using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class LatencyEntryRegression
{
    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(70);
    }
    public static async Task RunAsync(List<string> checks)
    {
        Check(Choices.Latencies.All(c => c.Value is not "adaptive"), "global latency choices omit adaptive", checks);
        Check(Choices.ReceiverLatencies.All(c => c.Value is not "adaptive"), "receiver latency choices omit adaptive", checks);

        var receiver = new Receiver("legacy", "Legacy fixed", "127.0.0.1");
        var directory = Path.Combine(Path.GetTempPath(), "airflash-latency-entry", Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(directory, "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.WriteAllText(settingsFile, "{\"schema_version\":5,\"latency_mode\":\"adaptive\",\"receivers\":{\"legacy\":{\"latency_mode\":\"adaptive\"}}}");
            var persisted = new SettingsStore(settingsFile);
            var loaded = persisted.Load();
            Check(loaded.LatencyMode == "normal" && loaded.ReadOptions("legacy").LatencyMode == "normal",
                "legacy adaptive settings load as normal", checks);
            await using var app = new AppViewModel(persisted, new MockDiscovery { Items = [receiver] }, new MockAutostart(), new MockAudio(), new MockFactory(), Application.Current.Dispatcher);
            app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump();
            var settingsWindow = new SettingsWindow(app);
            settingsWindow.ViewModel.SelectedPage = SettingsViewModel.StreamingPage;
            settingsWindow.Show(); await Pump();
            try
            {
                var adaptive = Descendants(settingsWindow).OfType<RadioButton>()
                    .FirstOrDefault(r => r.Content?.ToString() == L.Get("Adaptive latency"));
                Check(adaptive is null, "settings page does not render an adaptive latency radio", checks);
                var row = settingsWindow.ViewModel.Receivers.Single(r => r.Receiver.Id == receiver.Id);
                Check(settingsWindow.ViewModel.Draft.LatencyMode == "normal" && row.Options.LatencyMode == "normal",
                    "legacy adaptive display falls back to normal", checks);
            }
            finally { await settingsWindow.ViewModel.ApplicationCompleted; settingsWindow.Close(); }
            persisted.Save(loaded);
            using var json = JsonDocument.Parse(File.ReadAllText(settingsFile));
            Check(json.RootElement.GetProperty("latency_mode").GetString() == "normal"
                && json.RootElement.GetProperty("receivers").GetProperty("legacy").GetProperty("latency_mode").GetString() == "normal",
                "legacy adaptive values persist as normal", checks);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
