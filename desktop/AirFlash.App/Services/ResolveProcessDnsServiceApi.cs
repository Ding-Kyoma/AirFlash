using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AirFlash.Core;

namespace AirFlash.App.Services;

internal sealed record ResolveRequest(string Name, string Type, uint InterfaceIndex);
internal sealed record ResolveReply(int Version, uint Status, ServiceRecord? Record);

internal interface IResolveChild : IDisposable
{
    // The object owns its process handle before starting. A throwing Start may
    // still have associated a process; only Started=false proves no association.
    bool Started { get; }
    void Start();
    Task<string?> ReadReplyAsync();
    Task WaitForExitAsync();
    void Kill();
}
internal interface IResolveChildFactory { IResolveChild Create(ResolveRequest request); }

// DnsServiceResolve does not document a final callback. Run each query in its own
// address space instead of guessing when cancellation makes its inputs reclaimable.
// Browse retains its documented ERROR_CANCELLED lifetime in the parent.
internal sealed class ResolveProcessDnsServiceApi(IDnsServiceApi browse, IResolveChildFactory children) : IDnsServiceApi
{
    internal const int ProcessLimit = 8;
    private static readonly object Admission = new();
    private static readonly ConcurrentDictionary<long, Operation> Owners = new();
    internal static int ProcessOwnerCount => Owners.Count;
    public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback)
        => browse.CreateBrowse(type, index, callback);
    public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback)
    {
        ResolveWire.ValidateRequest(new(name, type, index));
        ArgumentNullException.ThrowIfNull(callback);
        lock (Admission)
        {
            // Starting, running, and killed-but-not-observed-exited all count.
            if (Owners.Count >= ProcessLimit) throw new DnsServiceCapacityException(DnsServiceCapacityReason.ResolveConcurrency);
            var token = DnsServiceOwnership.Acquire();
            try
            {
                var operation = new Operation(token, new(name, type, index), children.Create(new(name, type, index)), callback);
                Owners[token] = operation;
                return operation;
            }
            catch { DnsServiceOwnership.Release(); throw; }
        }
    }

    private sealed class Operation(long token, ResolveRequest request, IResolveChild child, Action<uint, ServiceRecord?> callback) : IDnsServiceOperation
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _invoked, _retired, _settled;
        private int _delivered;
        public long Token => token;
        public string Name => request.Name;
        public bool IsResolve => true;
        public bool IsSettled { get { lock (_gate) return _settled; } }
        public event Action? Settled;
        public event Action<string>? CancellationFailed;
        public event Action<string>? CallbackFailed;
        public uint Start()
        {
            lock (_gate)
            {
                if (_settled || _retired) return 1223;
                if (_invoked) return 9506;
                _invoked = true;
            }
            _ = Task.Run(Run);
            return 9506;
        }
        public uint Cancel()
        {
            bool unstarted;
            lock (_gate) { _retired = true; unstarted = !_invoked; }
            _cancel.TrySetResult();
            if (unstarted) Settle();
            return 0;
        }
        private async Task Run()
        {
            Task? exit = null;
            try
            {
                bool retired;
                lock (_gate) retired = _retired;
                if (retired) { Settle(); return; }
                child.Start();
                if (!child.Started) throw new InvalidOperationException("The DNS resolve helper did not start.");
                exit = child.WaitForExitAsync(); // No cancellation token: cancellation is not exit.
                var reply = child.ReadReplyAsync();
                _ = reply.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var winner = await Task.WhenAny(reply, exit, _cancel.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (winner == reply || winner == exit)
                {
                    // An exited process may still have a buffered complete reply.
                    if (winner == exit) await exit.ConfigureAwait(false);
                    var text = await reply.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    var parsed = ResolveWire.Parse(text, request);
                    Deliver(parsed.Status, parsed.Record);
                }
                else if (winner != _cancel.Task)
                    Fail("The DNS resolve helper exceeded its process deadline.");
            }
            catch (Exception error)
            {
                Fail("DNS resolve helper: " + error.Message);
            }
            finally
            {
                // A result, failed read, cancellation, or timeout never releases the
                // process owner. Kill is asynchronous; successful exit observation
                // is the only post-start production settlement path.
                if (!child.Started) Settle();
                else
                {
                    try { child.Kill(); }
                    catch (Exception error) { Notify(CancellationFailed, "DNS resolve helper termination failed: " + error.Message); }
                    try
                    {
                        await (exit ?? child.WaitForExitAsync()).ConfigureAwait(false);
                        Settle();
                    }
                    catch (Exception error)
                    {
                        Notify(CancellationFailed, "DNS resolve helper exit was not established: " + error.Message);
                        // Keep the exact process object rooted. A later successful
                        // observation can recover its slot; never substitute a PID.
                        _ = ObserveLater();
                    }
                }
            }
        }
        private async Task ObserveLater()
        {
            while (!IsSettled)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                try { await child.WaitForExitAsync().ConfigureAwait(false); Settle(); }
                catch { } // Already reported; retain admission until exit is proven.
            }
        }
        private void Fail(string message)
        {
            lock (_gate) { if (_retired || _settled) return; }
            Notify(CallbackFailed, message); Deliver(1, null);
        }
        private void Deliver(uint status, ServiceRecord? record)
        {
            lock (_gate)
            {
                if (_retired || _settled || Interlocked.Exchange(ref _delivered, 1) != 0) return;
            }
            try { callback(status, record); }
            catch (Exception error) { Notify(CallbackFailed, "DNS resolve result observer: " + error.Message); }
        }
        private void Settle()
        {
            Action? observers;
            lock (_gate)
            {
                if (_settled) return;
                _settled = true; observers = Settled; Settled = null;
                CancellationFailed = null; CallbackFailed = null;
            }
            // Dispose is cleanup, not evidence of exit. It happens only afterwards.
            try { child.Dispose(); }
            catch { }
            Owners.TryRemove(token, out _); DnsServiceOwnership.Release();
            if (observers is not null)
                foreach (Action observer in observers.GetInvocationList()) try { observer(); } catch { }
        }
        private static void Notify(Action<string>? observers, string message)
        {
            if (observers is null) return;
            foreach (Action<string> observer in observers.GetInvocationList()) try { observer(message); } catch { }
        }
    }
}

internal static class ResolveWire
{
    internal const int MaximumReplyBytes = 65536;
    internal static void ValidateRequest(ResolveRequest request)
    {
        if (request.Name.Length is 0 or > 1024 || request.Name.Contains('\0')
            || request.Type is not ("_airplay._tcp.local" or "_raop._tcp.local"))
            throw new InvalidOperationException("Invalid DNS resolve helper request.");
    }
    internal static ResolveReply Parse(string? text, ResolveRequest request)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) > MaximumReplyBytes)
            throw new InvalidOperationException("The DNS resolve helper returned no bounded reply.");
        var reply = JsonSerializer.Deserialize<ResolveReply>(text) ?? throw new InvalidOperationException("Invalid DNS resolve helper reply.");
        if (reply.Version != 1 || reply.Status == 0 && reply.Record is null || reply.Status != 0 && reply.Record is not null)
            throw new InvalidOperationException("Invalid DNS resolve helper reply version or status.");
        if (reply.Record is { } record)
        {
            if (record.Instance is null || record.ServiceType is null
                || !record.Instance.Equals(request.Name, StringComparison.OrdinalIgnoreCase)
                || !record.ServiceType.Equals(request.Type, StringComparison.OrdinalIgnoreCase)
                || !IPAddress.TryParse(record.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
                || record.Port is <= 0 or > 65535 || record.Txt is null || record.Txt.Count > 256
                || record.Txt.Any(pair => pair.Key.Length > 4096 || pair.Value is null || pair.Value.Length > 4096))
                throw new InvalidOperationException("Invalid DNS resolve helper record.");
            // JSON dictionary deserialization loses the native TXT key comparer.
            // Keep key lookup case-insensitive without changing any TXT values.
            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in record.Txt)
                if (!txt.TryAdd(pair.Key, pair.Value))
                    throw new InvalidOperationException("Ambiguous DNS resolve helper TXT keys.");
            reply = reply with { Record = record with { Txt = txt } };
        }
        return reply;
    }
    internal static async Task<string?> ReadLineAsync(Stream stream)
    {
        // Read one byte beyond the limit, then reject. No unbounded ReadLine or
        // subprocess output accumulation, even for a corrupt helper.
        using var output = new MemoryStream();
        var bytes = new byte[1024];
        while (output.Length <= MaximumReplyBytes)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, MaximumReplyBytes + 1 - output.Length))).ConfigureAwait(false);
            if (count == 0) return output.Length == 0 ? null : throw new InvalidOperationException("Incomplete DNS resolve helper reply.");
            var newline = Array.IndexOf(bytes, (byte)'\n', 0, count);
            output.Write(bytes, 0, newline < 0 ? count : newline);
            if (output.Length > MaximumReplyBytes) break;
            if (newline >= 0) return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimEnd('\r');
        }
        throw new InvalidOperationException("The DNS resolve helper reply is too large.");
    }
}

internal sealed class ResolveChildFactory(string processPath, string assemblyPath, string? mock = null) : IResolveChildFactory
{
    internal static ResolveChildFactory ForApplication()
    {
        // This is called by the application's Windows constructor, never by a
        // source-linked Core fixture. Do not accidentally launch a test runner.
        var assembly = typeof(ResolveChildFactory).Assembly;
        if (assembly.GetName().Name != "AirFlash") throw new InvalidOperationException("The DNS helper must use the AirFlash entry point.");
        return new(Environment.ProcessPath ?? throw new InvalidOperationException("AirFlash process path is unavailable."),
            Path.Combine(AppContext.BaseDirectory, "AirFlash.dll"));
    }
    public IResolveChild Create(ResolveRequest request)
    {
        var info = StartInfo(processPath, assemblyPath, request, mock);
        return new ResolveChild(new Process { StartInfo = info });
    }
    internal static ProcessStartInfo StartInfo(string host, string assembly, ResolveRequest request, string? mock = null)
    {
        ResolveWire.ValidateRequest(request);
        var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(assembly)) throw new InvalidOperationException("The AirFlash assembly path is unavailable.");
            info.ArgumentList.Add(assembly);
        }
        info.ArgumentList.Add("--dns-resolve-helper");
        info.ArgumentList.Add(request.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(request.Type); info.ArgumentList.Add(request.Name);
        if (mock is not null) { info.ArgumentList.Add("--mock"); info.ArgumentList.Add(mock); }
        return info;
    }
    private sealed class ResolveChild(Process process) : IResolveChild
    {
        public bool Started
        {
            get { try { return !process.SafeHandle.IsInvalid; } catch (InvalidOperationException) { return false; } }
        }
        public void Start() { if (!process.Start()) throw new InvalidOperationException("The DNS helper did not associate a process."); }
        public Task<string?> ReadReplyAsync() => ResolveWire.ReadLineAsync(process.StandardOutput.BaseStream);
        public Task WaitForExitAsync() => process.WaitForExitAsync(CancellationToken.None);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose() => process.Dispose();
    }
}
