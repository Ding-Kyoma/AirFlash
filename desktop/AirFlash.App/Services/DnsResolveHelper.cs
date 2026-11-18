using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AirFlash.Core;

namespace AirFlash.App.Services;

// This entry point runs before WPF, logging, settings, engine extraction, or COM.
// Its only production native operation is one interface-scoped DNS resolve.
internal static class DnsResolveHelper
{
    internal static int Run(string[] args)
    {
        // A dedicated thread still terminates a blocked native Start. The helper
        // starts no descendants. Parent death therefore leaves at most this finite
        // query lifetime; the parent independently kills and observes exit too.
        var mockIndex = Array.IndexOf(args, "--mock");
        var mock = mockIndex >= 0 && mockIndex + 1 < args.Length ? args[mockIndex + 1] : null;
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(mock == "watchdog" ? 250 : 6000);
            using var self = Process.GetCurrentProcess();
            try { self.Kill(); }
            catch { Environment.FailFast("The DNS resolve helper watchdog could not terminate its process."); }
        }) { IsBackground = true, Name = "DNS resolve lifetime" };
        watchdog.Start();
        try
        {
            if (args.Length < 4 || !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return 2;
            var request = new ResolveRequest(args[3], args[2], index);
            ResolveWire.ValidateRequest(request);
            using var ready = new ManualResetEventSlim();
            var written = 0;
            void Reply(uint status, ServiceRecord? record)
            {
                if (Interlocked.Exchange(ref written, 1) != 0) return;
                try
                {
                    var reply = new ResolveReply(1, status, status == 0 ? record : null);
                    var text = JsonSerializer.Serialize(reply);
                    try { ResolveWire.Parse(text, request); }
                    catch { text = JsonSerializer.Serialize(new ResolveReply(1, 13, null)); }
                    Console.OutputEncoding = new UTF8Encoding(false);
                    // The wire byte limit excludes LF. Windows' default CRLF
                    // would add an extra counted byte to an exact-limit reply.
                    Console.Out.Write(text); Console.Out.Write('\n'); Console.Out.Flush();
                }
                finally { ready.Set(); }
            }
            if (mock is not null)
            {
                switch (mock)
                {
                    case "valid": Reply(0, new(request.Name, request.Type, "192.0.2.1", 7000, new Dictionary<string, string> { ["deviceid"] = "synthetic" })); break;
                    case "malformed": Console.Out.WriteLine("invalid-json"); Console.Out.Flush(); break;
                    case "failure": return 3;
                    case "watchdog": Thread.Sleep(Timeout.Infinite); break;
                    case "hold": Thread.Sleep(Timeout.Infinite); break;
                    default: return 2;
                }
                return 0;
            }
            var api = new NativeDnsServiceApi();
            var operation = api.CreateResolve(request.Name, request.Type, request.InterfaceIndex, Reply);
            operation.CallbackFailed += _ => Reply(13, null);
            var status = operation.Start();
            if (status != 9506) Reply(status == 0 ? 13u : status, null);
            ready.Wait();
            // No pending resolve input is reclaimed here. Process exit, observed
            // by the parent, frees this address space including its native owners.
            GC.KeepAlive(operation);
            return 0;
        }
        catch { return 2; }
    }
}

internal static class DnsResolveHelperCheck
{
    internal static int Run(string[] args)
    {
        // Receiver-free real-process checks. No application services are created.
        try
        {
            var outputIndex = Array.IndexOf(args, "--output");
            if (outputIndex < 0 || outputIndex + 1 >= args.Length) return 2;
            var assembly = Path.Combine(AppContext.BaseDirectory, "AirFlash.dll");
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("Application host is unavailable.");
            var request = new ResolveRequest("Synthetic._airplay._tcp.local", "_airplay._tcp.local", 42);
            var timings = new List<long>();
            for (var i = 0; i < 3; ++i)
            {
                var clock = Stopwatch.StartNew();
                using var child = new ResolveChildFactory(host, assembly, "valid").Create(request);
                child.Start();
                var reply = ResolveWire.Parse(child.ReadReplyAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(), request);
                child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (reply.Record?.Address != "192.0.2.1") throw new InvalidOperationException("The mock reply was not preserved.");
                timings.Add(clock.ElapsedMilliseconds);
            }
            long watchdogMilliseconds;
            {
                var clock = Stopwatch.StartNew();
                using var child = new ResolveChildFactory(host, assembly, "watchdog").Create(request);
                child.Start(); child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                watchdogMilliseconds = clock.ElapsedMilliseconds;
                if (child.ReadReplyAsync().GetAwaiter().GetResult() is not null) throw new InvalidOperationException("The watchdog mock unexpectedly replied.");
            }
            using (var child = new ResolveChildFactory(host, assembly, "hold").Create(request))
            {
                child.Start(); child.Kill();
                child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
            var accepted = new TaskCompletionSource<ResolveReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adapter = new ResolveProcessDnsServiceApi(new NativeDnsServiceApi(), new ResolveChildFactory(host, assembly, "valid"));
            var operation = adapter.CreateResolve(request.Name, request.Type, request.InterfaceIndex,
                (status, record) => accepted.TrySetResult(new(1, status, record)));
            operation.Settled += () => settled.TrySetResult();
            try
            {
                operation.Start();
                var reply = accepted.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                settled.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (reply.Status != 0 || reply.Record?.Address != "192.0.2.1" || ResolveProcessDnsServiceApi.ProcessOwnerCount != baseline)
                    throw new InvalidOperationException("The parent adapter did not preserve the result and settle after exit.");
            }
            finally { operation.Cancel(); }
            var output = Path.GetFullPath(args[outputIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, synthetic = true, completedHelpers = 6,
                parentAdapterSettled = operation.IsSettled, parentNativeOwners = NativeDnsServiceApi.CurrentOwnerCount,
                startupAndReplyMilliseconds = timings, watchdogMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch { return 1; }
    }
}
