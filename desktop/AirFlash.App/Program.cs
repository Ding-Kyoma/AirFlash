using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App;

internal static class Program
{
    internal static string? DiagnosticStartupDataDirectory { get; private set; }
    private static readonly string[] FocusedUiModes =
    [
        "--ui-discovery-smoke", "--ui-session-lifecycle-smoke", "--ui-diagnostics-smoke",
        "--ui-regression", "--ui-regression-smoke", "--receiver-settings-regression",
        "--ui-pairing-hint-smoke", "--ui-ownership-smoke", "--ui-mute-recovery-smoke", "--ui-sound-glow-smoke",
        "--ui-settings-recovery-smoke", "--ui-pairing-smoke", "--ui-password-smoke", "--ui-scroll-smoke"
    ];
    private static bool IsPrimaryUiMode(string option) => option is "--ui-smoke" or "--tray-smoke" or "--ui-perf";
    private static bool IsUiOption(string option) => option.StartsWith("--ui-", StringComparison.Ordinal)
        || option is "--tray-smoke" or "--tray-guid" or "--tray-window-id" or "--receiver-settings-regression";
    private static bool IsDnsHelperMode(string option) => option is "--dns-resolve-helper" or "--dns-resolve-helper-check";
    private static bool IsOtherDiagnosticMode(string option) => IsDnsHelperMode(option) || option is
        "--discovery-perf" or "--update-check" or "--self-check" or "--stability-check" or "--compat-check" or
        "--discovery-check" or "--audio-check";
    private static bool HasStartupValue(string option) => option is
        "--output" or "--self-check-output" or "--ui-language" or "--ui-culture" or "--tray-guid";
    // Advance over option values even when a path or language is spelled like a switch.
    private static IEnumerable<int> StartupOptionIndices(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            yield return index;
            if (HasStartupValue(args[index])) index++;
        }
    }
    internal static IEnumerable<string> StartupOptions(string[] args) => StartupOptionIndices(args).Select(index => args[index]);
    internal static string? StartupValue(string[] args, string option, string? alias = null)
    {
        foreach (var index in StartupOptionIndices(args))
            if (args[index] == option || args[index] == alias)
                return index + 1 < args.Length ? args[index + 1] : null;
        return null;
    }
    internal static bool IsDiagnostic(string[] args) => NeedsDiagnosticIsolation(args)
        || StartupOptions(args).Any(option => option.EndsWith("-check", StringComparison.Ordinal));
    internal static bool NeedsDiagnosticIsolation(string[] args) => StartupOptions(args).Any(option =>
        IsUiOption(option) || IsOtherDiagnosticMode(option)) || HasOrphanedDiagnosticOption(args);
    internal static bool HasOrphanedDiagnosticOption(string[] args)
    {
        var options = StartupOptions(args).ToArray();
        if (options.Skip(1).Any(IsDnsHelperMode)) return true;
        // A missing value can swallow the only UI marker. Fail before starting
        // the shell or another diagnostic. A real UI route can use such a value.
        if (!options.Any(IsUiOption) && args.Any(IsUiOption)) return true;
        return !options.Any(IsPrimaryUiMode) && !options.Any(IsOtherDiagnosticMode) && args.Any(IsOtherDiagnosticMode);
    }
    internal static string? FocusedUiMode(IEnumerable<string> options) => options.FirstOrDefault(FocusedUiModes.Contains);
    internal static void ValidateUiVerificationArguments(string[] args)
    {
        var options = StartupOptions(args).ToArray();
        if (!options.Any(IsUiOption)) return;
        var primary = options.Where(IsPrimaryUiMode).ToArray();
        if (primary.Length != 1)
            throw new ArgumentException("UI verification requires exactly one primary mode: --ui-smoke, --tray-smoke or --ui-perf.");
        if (options.Any(IsOtherDiagnosticMode) || options.Contains("--restart-after"))
            throw new ArgumentException("UI verification cannot be combined with another diagnostic or restart mode.");
        foreach (var index in StartupOptionIndices(args))
            if (HasStartupValue(args[index]) && index + 1 == args.Length)
                throw new ArgumentException(args[index] + " requires a value.");

        var focused = options.Where(FocusedUiModes.Contains).ToArray();
        if (focused.Length > 1) throw new ArgumentException("Select exactly one focused UI verification mode.");
        foreach (var option in options.Where(IsUiOption))
        {
            var allowed = option == primary[0] || option is "--ui-language" or "--ui-culture"
                || ((primary[0] is "--ui-smoke" or "--tray-smoke") && (option is "--tray-guid" or "--tray-window-id"))
                || (primary[0] == "--ui-smoke" && (FocusedUiModes.Contains(option)
                    || option is "--ui-scroll-hold" or "--ui-diagnostics-dpi" or "--ui-pairing-clipboard"));
            if (!allowed) throw new ArgumentException("UI verification option is invalid for " + primary[0] + ": " + option);
        }
        foreach (var (modifier, owner) in new[] { ("--ui-scroll-hold", "--ui-scroll-smoke"),
            ("--ui-diagnostics-dpi", "--ui-diagnostics-smoke"), ("--ui-pairing-clipboard", "--ui-pairing-smoke") })
            if (options.Contains(modifier) && focused.SingleOrDefault() != owner)
                throw new ArgumentException(modifier + " requires " + owner + ".");
    }
    internal static bool IsValidDnsHelperInvocation(string[] args) => args switch
    {
        ["--dns-resolve-helper", _, _, var name] => name.Contains('.'),
        ["--dns-resolve-helper", _, _, var name, "--mock", _] => name.Contains('.'),
        ["--dns-resolve-helper-check", "--output", var path] => !IsUiOption(path) && !IsOtherDiagnosticMode(path),
        _ => false
    };

    [STAThread]
    public static int Main(string[] args)
    {
        // DNS children must not initialize WPF or touch application data. Keep
        // this dispatch before restart coordination, logging or App construction.
        if (args.Length > 0 && IsDnsHelperMode(args[0]))
        {
            if (!IsValidDnsHelperInvocation(args)) return 2;
            return args[0] == "--dns-resolve-helper" ? DnsResolveHelper.Run(args) : DnsResolveHelperCheck.Run(args);
        }
        var diagnostic = NeedsDiagnosticIsolation(args);
        if (diagnostic)
        {
            // Establish a safe fallback before even diagnostic argument parsing can fail.
            AppPaths.DataDirectory = StartupOptions(args).Any(a => a is "--ui-smoke" or "--tray-smoke")
                ? Path.Combine(Path.GetTempPath(), "airplay-ui-smoke", "isolated-data")
                : Path.Combine(Path.GetTempPath(), "airflash-diagnostics", Guid.NewGuid().ToString("N"), "isolated-data");
            DiagnosticStartupDataDirectory = AppPaths.DataDirectory;
        }
        // Also works when the WPF resource dictionary or localization cannot initialize.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log($"Fatal stage=unhandled {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppPaths.Log($"Background task failed {e.Exception}");
        var stage = "arguments";
        try
        {
            if (HasOrphanedDiagnosticOption(args))
            {
                AppPaths.Log("Startup stage=arguments rejected a misplaced diagnostic mode.");
                return 2;
            }
            // All diagnostics keep startup failures alongside their isolated output.
            if (diagnostic)
            {
                if (StartupValue(args, "--output", "--self-check-output") is { } output)
                    AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
                        StartupOptions(args).Contains("--compat-check") ? "compat-logs" : "isolated-data");
                DiagnosticStartupDataDirectory = AppPaths.DataDirectory;
            }
            ValidateUiVerificationArguments(args);
            RestartService.WaitForPreviousProcess(args);
            stage = "platform";
            AppPaths.Log($"Startup stage={stage} version={AppPaths.DisplayVersion} os={Environment.OSVersion.Version} architecture={RuntimeInformation.ProcessArchitecture}");
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
