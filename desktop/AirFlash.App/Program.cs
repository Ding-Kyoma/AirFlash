using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App;

internal static class Program
{
    internal static string? DiagnosticStartupDataDirectory { get; private set; }
    internal static bool IsDiagnostic(string[] args) => args.Any(a => a.EndsWith("-check", StringComparison.Ordinal)
        || a is "--ui-smoke" or "--tray-smoke" or "--ui-perf" or "--discovery-perf");
    // Match the modes actually dispatched by App.OnStartup. The broad error-reporting
    // predicate above must not redirect a normal startup merely because a value ends in -check.
    internal static bool NeedsDiagnosticIsolation(string[] args) => args.Any(a => a is
        "--discovery-perf" or "--update-check" or "--self-check" or "--stability-check" or "--compat-check" or
        "--discovery-check" or "--audio-check" or "--ui-smoke" or "--tray-smoke" or "--ui-perf");
    [STAThread]
    public static int Main(string[] args)
    {
        var diagnostic = NeedsDiagnosticIsolation(args);
        if (diagnostic)
        {
            // Establish a safe fallback before even diagnostic argument parsing can fail.
            AppPaths.DataDirectory = args.Any(a => a is "--ui-smoke" or "--tray-smoke")
                ? Path.Combine(Path.GetTempPath(), "airplay-ui-smoke", "isolated-data")
                : Path.Combine(Path.GetTempPath(), "airflash-diagnostics", Guid.NewGuid().ToString("N"), "isolated-data");
            DiagnosticStartupDataDirectory = AppPaths.DataDirectory;
        }
        // Also works when the WPF resource dictionary or localization cannot initialize.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log($"Fatal stage=unhandled {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppPaths.Log($"Background task failed {e.Exception}");
        var stage = "platform";
        try
        {
            // All diagnostics keep startup failures alongside their isolated output.
            if (diagnostic)
            {
                var index = Array.FindIndex(args, a => a is "--output" or "--self-check-output");
                if (index >= 0 && index + 1 < args.Length)
                    AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1]))!,
                        args.Contains("--compat-check") ? "compat-logs" : "isolated-data");
                DiagnosticStartupDataDirectory = AppPaths.DataDirectory;
            }
            AppPaths.Log($"Startup stage={stage} version={AppPaths.Version} os={Environment.OSVersion.Version} architecture={RuntimeInformation.ProcessArchitecture}");
            if (!WindowsCapabilities.Current.Supported) throw new PlatformNotSupportedException(WindowsCompatibility.Requirement);
            stage = "wpf-resources";
            var app = new App();
            app.InitializeComponent();
            AppPaths.Log($"Startup stage={stage} ready");
            return app.Run();
        }
        catch (Exception error)
        {
            AppPaths.Log($"Startup stage={stage} failed {error}");
            try { App.WriteOutput(args, new { ok = false, stage, error = error.ToString(), hresult = $"0x{error.HResult:X8}" }); }
            catch (Exception reportError) { AppPaths.Log($"Diagnostic output failed: {reportError.Message}"); }
            if (!IsDiagnostic(args))
                MessageBox(IntPtr.Zero, $"{error.Message}\n\nLogs: {AppPaths.LogDirectory}", "AirFlash startup failed", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string message, string caption, uint type);
}
