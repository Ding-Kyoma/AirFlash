using System.Diagnostics;
namespace AirFlash.App.Services;

public interface IRestartService
{
    void Schedule();
}
public sealed class RestartService : IRestartService
{
    public void Schedule()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the application executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        // Framework-dependent launches need the managed entry assembly as the first argument.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Path.Combine(AppContext.BaseDirectory, typeof(RestartService).Assembly.GetName().Name + ".dll");
            if (!File.Exists(assembly)) throw new IOException("Cannot locate the managed application entry point.");
            start.ArgumentList.Add(assembly);
        }
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Could not restart AirFlash.");
    }
    internal static void WaitForPreviousProcess(string[] args)
    {
        if (args.Length == 0 || args[0] != "--restart-after") return;
        if (args.Length != 3 || !int.TryParse(args[1], out var id) || !long.TryParse(args[2], out var startedAt) || id == Environment.ProcessId)
            throw new ArgumentException("Invalid restart arguments.");
        Process previous;
        try { previous = Process.GetProcessById(id); }
        catch (ArgumentException) { return; }
        using (previous)
        {
            if (previous.StartTime.ToUniversalTime().Ticks != startedAt) return;
            if (!previous.WaitForExit(30000)) throw new TimeoutException("AirFlash could not restart because the previous process is still running.");
        }
    }
}
