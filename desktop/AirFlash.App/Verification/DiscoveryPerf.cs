using System.Diagnostics;
using AirFlash.App.Services;
namespace AirFlash.App.Verification;

// Finite, opt-in native discovery verification; no audio, saved settings or tray.
internal static class DiscoveryPerf
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var process = Process.GetCurrentProcess();
        using var discovery = new WindowsDiscovery(log: AppPaths.Log);
        var failures = new List<string>(); var receiverCount = 0;
        discovery.Failed += failure => { lock (failures) failures.Add(failure.Message); };
        discovery.Changed += items => Volatile.Write(ref receiverCount, items.Count);
        var samples = new List<object>();
        async Task Sample(string phase, int seconds)
        {
            var lastCpu = process.TotalProcessorTime.TotalMilliseconds; var lastAt = Stopwatch.GetTimestamp();
            for (var i = 0; i < seconds; i++)
            {
                await Task.Delay(1000); process.Refresh();
                var now = Stopwatch.GetTimestamp(); var cpu = process.TotalProcessorTime.TotalMilliseconds;
                var nativeOwners = NativeDnsServiceApi.CurrentOwnerCount;
                var helperOwners = ResolveProcessDnsServiceApi.ProcessOwnerCount;
                samples.Add(new { phase, second = i + 1,
                    cpu_percent = 100 * (cpu - lastCpu) / (Stopwatch.GetElapsedTime(lastAt, now).TotalMilliseconds * Environment.ProcessorCount),
                    threads = process.Threads.Count, handles = process.HandleCount, private_bytes = process.PrivateMemorySize64,
                    active_queries = nativeOwners + helperOwners,
                    logical_queries = discovery.Snapshot.ActiveQueries,
                    retained_native_owners = nativeOwners,
                    retained_resolve_owners = NativeDnsServiceApi.RetainedResolveOwnerCount,
                    resolve_helper_owners = helperOwners,
                    receivers = Volatile.Read(ref receiverCount) });
                lastAt = now; lastCpu = cpu;
            }
        }
        try
        {
            discovery.Start(); await Sample("visible", 150); discovery.Stop();
            var stopped = discovery.Snapshot.ActiveQueries == 0;
            for (var i = 0; i < 25; i++)
            {
                discovery.Start(); await Task.Delay(400); discovery.Stop();
                stopped &= discovery.Snapshot.ActiveQueries == 0;
            }
            await Sample("hidden", 120);
            string[] errors; lock (failures) errors = failures.ToArray();
            var result = CreateReport(stopped, NativeDnsServiceApi.CurrentOwnerCount, ResolveProcessDnsServiceApi.ProcessOwnerCount, errors, samples);
            App.WriteOutput(args, result.Report);
            return result.Ok ? 0 : 1;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, resource_scope = "desktop-parent-only", resolve_helper_resources_included = false, error = error.ToString(), samples }); return 1; }
    }

    internal static (bool Ok, object Report) CreateReport(bool stopped, int nativeOwners, int helperOwners,
        IReadOnlyList<string> failures, IReadOnlyList<object> samples)
    {
        // Give asynchronous cancellation the hidden observation window to settle.
        var released = stopped && nativeOwners == 0 && helperOwners == 0;
        var ok = released && failures.Count == 0;
        return (ok, new
        {
            ok, resource_scope = "desktop-parent-only", resolve_helper_resources_included = false,
            all_queries_stopped = stopped, all_queries_released = released,
            retained_native_owners = nativeOwners, resolve_helper_owners = helperOwners,
            resolve_settlement = "observed-helper-exit", cycles = 25, failures, samples
        });
    }
}
