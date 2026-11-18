using System.ComponentModel;
using System.Net.NetworkInformation;
using AirFlash.Core;
namespace AirFlash.App.Services;

// Logical deadlines do not settle resource owners. Browse needs its documented
// final callback; production resolve needs observed exit of its helper process.
// Only a current epoch/token may change advertised or cached receiver state.
public sealed partial class WindowsDiscovery : IDiscoveryService, IDiscoveryDiagnostics
{
    private const uint PendingStatus = 9506;
    private readonly object _sync = new();
    private readonly object _publication = new();
    private readonly IDnsServiceApi _dns;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private readonly Func<IReadOnlyList<DiscoveryAdapter>> _adapters;
    private readonly Action<string> _log;
    private readonly bool _enableInfrastructure;
    private readonly bool _queueNative;
    private readonly Func<bool> _dnsAvailable;
    private readonly DiscoveryPolicy _policy;
    private readonly Dictionary<string, Advertisement> _advertised = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Owner> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, Owner> _owners = [];
    private readonly Dictionary<string, Browser> _browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_airplay._tcp.local"] = new("_airplay._tcp.local"),
        ["_raop._tcp.local"] = new("_raop._tcp.local")
    };
    private HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private string _interfaceId = "";
    private uint? _interfaceIndex;
    private bool? _observedInterfaceAvailability;
    private TimeSpan _nextAdapterCheck;
    private long _epoch;
    private long _resolveSequence;
    private long _restartSequence;
    private long _appliedRestartSequence;
    private readonly DiscoveryHistory _history = new();
    private DiscoveryDemand _demand;
    private bool _started;
    private bool _disposed;
    private bool _capacityReported;
    private bool _concurrencyReported;
    private bool _running;
    private Work? _deferredNativeWork;
    public event Action<IReadOnlyList<Receiver>>? Changed;
    public event Action<DiscoveryFailure>? Failed;
    public event Action<DiscoveryResolveAttempt>? ResolveSucceeded;
    public event Action<bool>? RunningChanged;

    internal WindowsDiscovery(IDnsServiceApi dns, TimeProvider clock,
        Func<IReadOnlyList<DiscoveryAdapter>> adapters, Action<string> log,
        bool enableInfrastructure = false, DiscoveryPolicy? policy = null, Func<bool>? dnsAvailable = null, bool queueNative = false)
    {
        _dns = dns; _clock = clock; _origin = clock.GetTimestamp(); _adapters = adapters;
        _log = log; _enableInfrastructure = enableInfrastructure; _policy = policy ?? new();
        _queueNative = enableInfrastructure || queueNative;
        _dnsAvailable = dnsAvailable ?? (() => true);
        if (_policy.ResolveTimeout <= TimeSpan.Zero || _policy.RefreshInterval <= TimeSpan.Zero
            || _policy.RetryBase <= TimeSpan.Zero
            || _policy.RetryMaximum < _policy.RetryBase || _policy.MaxOwners < 2
            || _policy.MaxResolveOwnersPerName < 1) throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private TimeSpan Now => _clock.GetElapsedTime(_origin, _clock.GetTimestamp());
    internal DiscoverySnapshot Snapshot
    {
        get { lock (_sync) return new(_epoch, _advertised.Count, _records.Count, _pending.Values.Count(owner => !owner.HasResult), _owners.Count,
            _pending.Count + _browsers.Values.Count(browser => browser.Owner is { Retired: false })); }
    }

    public void RecordDemand(DiscoveryDemand demand)
    {
        lock (_sync)
        {
            if (_disposed || demand == _demand) return;
            _demand = demand;
            TraceLocked(DiscoveryTraceEvent.DemandChanged);
        }
    }

    public DiscoveryDiagnosticsSnapshot SnapshotDiagnostics()
    {
        lock (_sync) return new(_epoch, _demand, _started && !_disposed, _running && !_disposed,
            _started && !_disposed ? _observedInterfaceAvailability : null,
            _advertised.Count, _records.Count, _pending.Values.Count(owner => !owner.HasResult), _owners.Count,
            _history.Recorded, _history.Overwritten, _history.Snapshot()) { Disposed = _disposed };
    }

    private void TraceLocked(DiscoveryTraceEvent kind, DiscoveryTraceReason reason = DiscoveryTraceReason.None,
        string? name = null, string? type = null, ServiceRecord? record = null, uint? ttl = null)
        => _history.Record(_clock.GetUtcNow(), Now, _epoch, kind, reason, _demand, _records.Count,
            name, type, record, ttl, record is not null && HasConflictingRaopIdentity(record, ReceiverAggregator.Identity(record)));

    public void SetInterface(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_sync)
        {
            if (_disposed || _interfaceId == id) return;
            _interfaceId = id;
            _observedInterfaceAvailability = null;
            ++_epoch; ++_restartSequence;
            foreach (var name in _records.Keys.ToArray()) RemoveRecordLocked(name, DiscoveryTraceReason.InterfaceChanged);
        }
        Publish();
        Restart(DiscoveryTraceReason.InterfaceChanged);
    }

    public void Start()
    {
        bool available;
        lock (_sync) { if (_disposed || _started) return; available = _dnsAvailable(); }
        if (!available)
        {
            lock (_sync) { if (!_disposed) TraceLocked(DiscoveryTraceEvent.Unavailable, DiscoveryTraceReason.DnsUnavailable); }
            Report(L.Get("Windows DNS-SD is unavailable. You can add a receiver manually and collect startup diagnostics."));
            return;
        }
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
            _observedInterfaceAvailability = null;
            TraceLocked(DiscoveryTraceEvent.Started, DiscoveryTraceReason.Requested);
            _nextAdapterCheck = Now + _policy.RefreshInterval;
            if (_enableInfrastructure)
            {
                NetworkChange.NetworkAddressChanged += NetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
                // The one-second maintenance tick bounds deadlines independently of the
                // less frequent adapter enumeration and successful-query refresh.
                _timer = _clock.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
        Restart(DiscoveryTraceReason.Requested);
    }

    public void Stop()
    {
        var work = new Work(); ITimer? timer;
        lock (_sync)
        {
            if (_disposed || !_started) return;
            _started = false; ++_epoch; ++_restartSequence; timer = _timer; _timer = null;
            foreach (var owner in _owners.Values) RetireLocked(owner, work);
            _pending.Clear(); _advertised.Clear(); _concurrencyReported = false;
            foreach (var browser in _browsers.Values) browser.Owner = null;
            TraceLocked(DiscoveryTraceEvent.Stopped, DiscoveryTraceReason.Requested);
            if (_enableInfrastructure)
            {
                NetworkChange.NetworkAddressChanged -= NetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            }
        }
        timer?.Dispose(); Run(work);
        // Suspended discovery preserves its catalog and does not revoke playback.
    }

    private void NetworkChanged(object? sender, EventArgs args) => Restart(DiscoveryTraceReason.NetworkAddressChanged);
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Restart(DiscoveryTraceReason.NetworkAvailabilityChanged);

    private void Restart(DiscoveryTraceReason reason)
    {
        string selected; long sequence;
        lock (_sync)
        {
            if (_disposed || !_started) return;
            selected = _interfaceId; sequence = ++_restartSequence;
            _observedInterfaceAvailability = null;
        }
        uint? index; string? error = null;
        try { index = DiscoveryInterface.ResolveIndex(selected, _adapters()); }
        catch (NetworkInformationException exception) { error = exception.Message; index = null; }
        var work = new Work();
        lock (_sync)
        {
            if (_disposed || !_started || sequence != _restartSequence || selected != _interfaceId) return;
            ++_epoch; _interfaceIndex = index; _nextAdapterCheck = Now + _policy.RefreshInterval;
            _appliedRestartSequence = sequence;
            _observedInterfaceAvailability = error is null ? index.HasValue : null;
            foreach (var owner in _owners.Values) RetireLocked(owner, work);
            _pending.Clear(); _advertised.Clear(); _concurrencyReported = false;
            // Cached entries remain visible on resume until advertised again.
            foreach (var name in _records.Keys.ToArray())
                _records[name] = _records[name] with { Expires = Now + TimeSpan.FromSeconds(10), ResumeGrace = true };
            TraceLocked(DiscoveryTraceEvent.Restarted, reason);
            foreach (var browser in _browsers.Values)
            {
                browser.Owner = null; browser.Failures = 0; browser.NextAttempt = Now;
            }
            if (error is not null) work.Errors.Add(new(error));
            if (index is null) work.Errors.Add(new(L.Get("The selected network interface is unavailable. Discovery is paused.")));
            else ScheduleLocked(Now, work);
        }
        Run(work);
    }

    internal void Tick()
    {
        string selected; uint? previous; long epoch; long restartSequence; TimeSpan nextAdapterCheck; bool checkAdapter;
        lock (_sync)
        {
            if (_disposed || !_started) return;
            selected = _interfaceId; previous = _interfaceIndex; epoch = _epoch;
            checkAdapter = Now >= _nextAdapterCheck;
            if (checkAdapter)
            {
                _nextAdapterCheck = Now + _policy.RefreshInterval;
                _observedInterfaceAvailability = null;
            }
            restartSequence = _restartSequence;
            nextAdapterCheck = _nextAdapterCheck;
        }
        if (checkAdapter)
        {
            try
            {
                var current = DiscoveryInterface.ResolveIndex(selected, _adapters());
                lock (_sync)
                {
                    if (_disposed || epoch != _epoch || selected != _interfaceId) return;
                    // Restart advances the epoch only after its lookup finishes.
                    if (current == previous && restartSequence == _restartSequence
                        && restartSequence == _appliedRestartSequence && nextAdapterCheck == _nextAdapterCheck)
                        _observedInterfaceAvailability = current.HasValue;
                }
                if (current != previous) { Restart(DiscoveryTraceReason.AdapterChanged); return; }
            }
            catch (NetworkInformationException exception)
            {
                Report(exception.Message); Restart(DiscoveryTraceReason.AdapterLookupFailed); return;
            }
        }
        var work = new Work();
        lock (_sync)
        {
            if (_disposed || !_started || epoch != _epoch) return;
            var now = Now;
            foreach (var name in _advertised.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                RemoveAdvertisementLocked(name, work, DiscoveryTraceReason.PtrExpired);
            foreach (var name in _records.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            {
                RemoveRecordLocked(name, _records[name].ResumeGrace ? DiscoveryTraceReason.ResumeGraceExpired : DiscoveryTraceReason.CacheExpired);
                work.Publish = true;
            }
            foreach (var owner in _pending.Values.Where(owner => !owner.HasResult && owner.Deadline <= now).ToArray())
            {
                FailResolveLocked(owner, now, work, DiscoveryTraceReason.ResolveTimeout);
                work.Errors.Add(ResolveFailureLocked(owner, now, DiscoveryFailureReason.ResolveTimeout));
            }
            ScheduleLocked(now, work);
        }
        Run(work);
    }

    private void ScheduleLocked(TimeSpan now, Work work)
    {
        if (_disposed || !_started || _interfaceIndex is not uint index) return;
        foreach (var browser in _browsers.Values)
        {
            if (browser.Owner is not null || browser.NextAttempt > now) continue;
            if (!HasCapacityLocked(null, work)) continue;
            var weak = new WeakReference<WindowsDiscovery>(this); Owner? owner = null;
            try
            {
                var operation = _dns.CreateBrowse(browser.Type, index, (status, announcements) =>
                {
                    if (owner is not null && weak.TryGetTarget(out var target)) target.Browse(owner, status, announcements);
                });
                owner = new(operation, browser.Type, browser.Type, _epoch, false);
                RegisterLocked(owner); browser.Owner = owner; work.Starts.Add(owner); _capacityReported = false;
            }
            catch (DnsServiceCapacityException) { CapacityLocked(work); }
        }
        foreach (var advertisement in _advertised.Values)
        {
            if (_pending.ContainsKey(advertisement.Name) || advertisement.NextAttempt > now || advertisement.Expires <= now) continue;
            if (!HasCapacityLocked(advertisement.Name, work)) continue;
            var weak = new WeakReference<WindowsDiscovery>(this); Owner? owner = null;
            try
            {
                var operation = _dns.CreateResolve(advertisement.Name, advertisement.Type, index, (status, record) =>
                {
                    if (owner is not null && weak.TryGetTarget(out var target)) target.Resolved(owner, status, record);
                });
                owner = new(operation, advertisement.Name, advertisement.Type, _epoch, true)
                {
                    Deadline = now + _policy.ResolveTimeout,
                    ResolveSequence = ++_resolveSequence
                };
                RegisterLocked(owner); _pending[advertisement.Name] = owner; work.Starts.Add(owner); _capacityReported = false;
                advertisement.ConcurrencyDeferredSince = null; _concurrencyReported = false;
            }
            catch (DnsServiceCapacityException error) when (error.Reason == DnsServiceCapacityReason.ResolveConcurrency)
            {
                // A full helper batch is ordinary backpressure. Keep the name due
                // until maintenance retries after exact process exit frees a slot.
                advertisement.ConcurrencyDeferredSince ??= now;
                if (!_concurrencyReported && now - advertisement.ConcurrencyDeferredSince.Value >= _policy.ResolveTimeout)
                {
                    _concurrencyReported = true;
                    work.Errors.Add(new(L.Get("Receiver discovery is waiting for earlier DNS queries to finish. It will retry automatically; restart AirFlash if it remains paused.")));
                }
            }
            catch (DnsServiceCapacityException) { CapacityLocked(work); }
        }
    }

    private bool HasCapacityLocked(string? name, Work work)
    {
        if (_owners.Count < _policy.MaxOwners && (name is null || _owners.Values.Count(owner => owner.Resolve
            && owner.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) < _policy.MaxResolveOwnersPerName)) return true;
        CapacityLocked(work); return false;
    }

    private void CapacityLocked(Work work)
    {
        if (_capacityReported) return;
        _capacityReported = true;
        work.Errors.Add(new(L.Get("Some discovery requests are paused because native DNS ownership cannot be safely released. Discovery resumes if ownership settles; restarting the app is the final recovery.")));
    }

    private void RegisterLocked(Owner owner)
    {
        var weak = new WeakReference<WindowsDiscovery>(this); var token = owner.Operation.Token;
        owner.SettledHandler = () => { if (weak.TryGetTarget(out var target)) target.Settled(token); };
        owner.CancellationFailedHandler = message =>
        {
            if (weak.TryGetTarget(out var target)) target.CancellationFailed(token, message);
        };
        owner.CallbackFailedHandler = message =>
        {
            if (weak.TryGetTarget(out var target)) target.CallbackFailed(token, message);
        };
        _owners.Add(token, owner);
        owner.Operation.Settled += owner.SettledHandler;
        owner.Operation.CancellationFailed += owner.CancellationFailedHandler;
        owner.Operation.CallbackFailed += owner.CallbackFailedHandler;
    }

    private void Browse(Owner owner, uint status, IReadOnlyList<DnsAnnouncement> announcements)
    {
        var work = new Work();
        lock (_sync)
        {
            if (_disposed || owner.Retired || owner.Epoch != _epoch
                || !_browsers.TryGetValue(owner.Type, out var browser) || !ReferenceEquals(browser.Owner, owner)) return;
            var now = Now;
            if (status != 0)
            {
                FailBrowseLocked(owner, now, work);
                work.Errors.Add(new(L.Format("Receiver discovery failed: {0}. You can add a receiver manually.", new Win32Exception((int)status).Message)));
            }
            else
            {
                browser.Failures = 0;
                foreach (var announcement in announcements)
                {
                    if (string.IsNullOrEmpty(announcement.Name)) continue;
                    if (announcement.Ttl == 0) { RemoveAdvertisementLocked(announcement.Name, work, DiscoveryTraceReason.PtrGoodbye); continue; }
                    if (_advertised.TryGetValue(announcement.Name, out var advertised)) advertised.Expires = now + TimeSpan.FromSeconds(Math.Min(announcement.Ttl, 86400u));
                    else
                    {
                        _advertised[announcement.Name] = new(announcement.Name, owner.Type, now + TimeSpan.FromSeconds(Math.Min(announcement.Ttl, 86400u)), now);
                        TraceLocked(DiscoveryTraceEvent.AdvertisementAdded, name: announcement.Name, type: owner.Type, ttl: Math.Min(announcement.Ttl, 86400u));
                    }
                    if (_records.TryGetValue(announcement.Name, out var cached))
                    {
                        _records[announcement.Name] = cached with { Expires = _advertised[announcement.Name].Expires, ResumeGrace = false };
                        if (cached.ResumeGrace)
                            TraceLocked(DiscoveryTraceEvent.CachedRecordTtlRefreshed, name: announcement.Name,
                                type: owner.Type, ttl: Math.Min(announcement.Ttl, 86400u));
                    }
                }
                ScheduleLocked(now, work);
            }
        }
        Run(work);
    }

    private void Resolved(Owner owner, uint status, ServiceRecord? record)
    {
        var work = new Work();
        lock (_sync)
        {
            // A copied helper result completes logical resolution, not resource
            // ownership. The process stays counted until its exit is observed.
            if (_disposed || owner.Retired || owner.Epoch != _epoch || !_pending.TryGetValue(owner.Name, out var pending)
                || !ReferenceEquals(pending, owner) || !_advertised.TryGetValue(owner.Name, out var advertisement)) return;
            var now = Now;
            if (advertisement.Expires <= now) RemoveAdvertisementLocked(owner.Name, work, DiscoveryTraceReason.PtrExpired);
            else if (!owner.HasResult && owner.Deadline <= now)
            {
                FailResolveLocked(owner, now, work, DiscoveryTraceReason.ResolveTimeout);
                work.Errors.Add(ResolveFailureLocked(owner, now, DiscoveryFailureReason.ResolveTimeout));
            }
            else if (status != 0 || record is null || record.Port is <= 0 or > 65535
                || string.IsNullOrWhiteSpace(record.Address) || !record.Instance.Equals(owner.Name, StringComparison.OrdinalIgnoreCase)
                || !record.ServiceType.Equals(owner.Type, StringComparison.OrdinalIgnoreCase))
            {
                FailResolveLocked(owner, now, work);
                work.Errors.Add(ResolveFailureLocked(owner, now, DiscoveryFailureReason.ResolveFailed));
            }
            else
            {
                owner.HasResult = true; advertisement.Failures = 0;
                advertisement.NextAttempt = now + _policy.RefreshInterval;
                _pending.Remove(owner.Name);
                RetireLocked(owner, work);
                if (!_records.TryGetValue(owner.Name, out var previous) || !Equal(previous.Record, record))
                {
                    work.Logs.Add("Discovery resolved: " + Describe(record));
                    work.Publish = true;
                }
                _records[owner.Name] = new(record, advertisement.Expires, owner.ResolveSequence);
                TraceLocked(DiscoveryTraceEvent.ResolveSucceeded, record: record);
                work.Resolutions.Add(ResolveAttempt(owner));
            }
        }
        Run(work);
    }

    private void RemoveAdvertisementLocked(string name, Work work, DiscoveryTraceReason reason)
    {
        if (_advertised.Remove(name, out var advertised))
        {
            _concurrencyReported = false;
            TraceLocked(DiscoveryTraceEvent.AdvertisementRemoved, reason, name, advertised.Type);
        }
        if (RemoveRecordLocked(name, reason)) work.Publish = true;
        if (_pending.Remove(name, out var pending)) RetireLocked(pending, work);
    }

    private bool RemoveRecordLocked(string name, DiscoveryTraceReason reason)
    {
        if (!_records.Remove(name, out var cached)) return false;
        TraceLocked(DiscoveryTraceEvent.RecordRemoved, reason, record: cached.Record);
        return true;
    }

    private void FailResolveLocked(Owner owner, TimeSpan now, Work work, DiscoveryTraceReason reason = DiscoveryTraceReason.ResolveFailed)
    {
        if (_pending.TryGetValue(owner.Name, out var pending) && ReferenceEquals(pending, owner))
        {
            _pending.Remove(owner.Name);
            if (_advertised.TryGetValue(owner.Name, out var advertisement))
                advertisement.NextAttempt = now + Backoff(++advertisement.Failures);
            TraceLocked(DiscoveryTraceEvent.ResolveFailed, reason, owner.Name, owner.Type);
        }
        RetireLocked(owner, work);
    }

    private DiscoveryFailure ResolveFailureLocked(Owner owner, TimeSpan now, DiscoveryFailureReason reason)
    {
        DiscoveryResolveContext? context = null;
        if (_records.TryGetValue(owner.Name, out var cached) && cached.Expires > now
            && cached.Record.ServiceType.Equals(owner.Type, StringComparison.OrdinalIgnoreCase))
        {
            var record = cached.Record;
            var identity = ReceiverAggregator.Identity(record);
            if (ReceiverIdentity.IsBroadcast(identity) && !HasConflictingRaopIdentity(record, identity))
                context = new(identity, record.Address, record.Port, record.Txt.GetValueOrDefault("tsid", "").Trim());
        }
        var message = reason == DiscoveryFailureReason.ResolveTimeout
            ? L.Format("Receiver resolution timed out: {0}. Discovery will retry with backoff.", owner.Name)
            : L.Format("Receiver resolution failed: {0}. Discovery will retry with backoff.", owner.Name);
        return new(message, reason, context) { Attempt = ResolveAttempt(owner) };
    }

    private static DiscoveryResolveAttempt ResolveAttempt(Owner owner) => new(owner.Name, owner.Type, owner.Epoch, owner.ResolveSequence);

    private static bool HasConflictingRaopIdentity(ServiceRecord record, string identity)
    {
        var separator = record.Instance.IndexOf('@');
        if (!record.ServiceType.StartsWith("_raop.", StringComparison.OrdinalIgnoreCase) || separator <= 0) return false;
        var prefix = ReceiverIdentity.Normalize(record.Instance[..separator]);
        return prefix.Length == 12 && prefix.All(Uri.IsHexDigit) && prefix != ReceiverIdentity.Normalize(identity);
    }

    private void FailBrowseLocked(Owner owner, TimeSpan now, Work work, DiscoveryTraceReason reason = DiscoveryTraceReason.None)
    {
        if (_browsers.TryGetValue(owner.Type, out var browser) && ReferenceEquals(browser.Owner, owner))
        {
            browser.Owner = null; browser.NextAttempt = now + Backoff(++browser.Failures);
            TraceLocked(DiscoveryTraceEvent.BrowseFailed, reason, type: owner.Type);
        }
        RetireLocked(owner, work);
    }

    private TimeSpan Backoff(int failures) => TimeSpan.FromTicks((long)Math.Min(_policy.RetryMaximum.Ticks,
        _policy.RetryBase.Ticks * Math.Pow(2, Math.Min(failures - 1, 30))));

    private void RetireLocked(Owner owner, Work work)
    {
        owner.Retired = true;
        if (owner.CancelRequested || owner.Operation.IsSettled) return;
        owner.CancelRequested = true; work.Cancels.Add(owner.Operation);
    }

    private void Settled(long token)
    {
        var work = new Work();
        lock (_sync)
        {
            if (!_owners.Remove(token, out var owner)) return;
            owner.Operation.Settled -= owner.SettledHandler;
            owner.Operation.CancellationFailed -= owner.CancellationFailedHandler;
            owner.Operation.CallbackFailed -= owner.CallbackFailedHandler;
            if (_disposed || owner.Retired || owner.Epoch != _epoch) return;
            // A non-pending Start can settle the adapter before its return value is
            // observed. Retire now, but retain the reason so Run reports that result.
            owner.UnexpectedSettlement = true;
            if (owner.Resolve) FailResolveLocked(owner, Now, work, DiscoveryTraceReason.QueryEndedUnexpectedly);
            else FailBrowseLocked(owner, Now, work, DiscoveryTraceReason.QueryEndedUnexpectedly);
        }
        Run(work);
    }

    private void CancellationFailed(long token, string message)
    {
        lock (_sync) { if (_disposed || !_owners.ContainsKey(token)) return; }
        Report(L.Format("DNS cancellation failed: {0}. Native resources remain safely owned; restarting the app is the final recovery if completion cannot be established.", message));
    }

    private void CallbackFailed(long token, string message)
    {
        lock (_sync) { if (_disposed || !_owners.ContainsKey(token)) return; }
        Report(L.Format("Receiver discovery: {0}", message));
    }

    private void Run(Work work)
    {
        if (Monitor.IsEntered(_publication))
        {
            // External subscribers may restart/dispose reentrantly. Native cancellation
            // can wait for a concurrent callback trying to publish, so defer only native
            // calls until the outermost publication releases its lock. Coalescing by token
            // and pruning settled owners keeps this queue bounded by native admission.
            if (work.Cancels.Count != 0 || work.Starts.Count != 0)
            {
                var deferred = _deferredNativeWork ??= new();
                deferred.Cancels.RemoveAll(operation => operation.IsSettled);
                deferred.Starts.RemoveAll(owner => owner.Operation.IsSettled);
                foreach (var operation in work.Cancels)
                    if (!operation.IsSettled && deferred.Cancels.All(existing => existing.Token != operation.Token))
                        deferred.Cancels.Add(operation);
                foreach (var owner in work.Starts)
                    if (!owner.Operation.IsSettled && deferred.Starts.All(existing => existing.Operation.Token != owner.Operation.Token))
                        deferred.Starts.Add(owner);
            }
        }
        else DispatchNative(work);
        foreach (var message in work.Logs) Log(message);
        foreach (var error in work.Errors) Report(error);
        foreach (var resolution in work.Resolutions) PublishResolution(resolution);
        if (work.Publish) Publish();
        PublishRunning();
    }

    private void DrainDeferredNative()
    {
        if (Monitor.IsEntered(_publication)) return;
        while (true)
        {
            Work? work;
            lock (_publication) { work = _deferredNativeWork; _deferredNativeWork = null; }
            if (work is null) return;
            DispatchNative(work);
        }
    }

    private void DispatchNative(Work work)
    {
        if (!_queueNative) { RunNative(work); return; }
        // Start/Cancel may block. Production callers include WPF visibility changes.
        // Never execute those calls on the dispatcher or under a publication frame.
        if (work.Cancels.Count != 0 || work.Starts.Count != 0)
            ThreadPool.UnsafeQueueUserWorkItem(_ => RunNative(work), (object?)null);
    }

    private void PublishRunning()
    {
        try
        {
            lock (_publication)
            {
                bool running;
                lock (_sync)
                {
                    running = !_disposed && _started && _interfaceIndex is not null
                        && _browsers.Values.Any(browser => browser.Owner is { Retired: false });
                    if (running == _running) return;
                    _running = running;
                }
                InvokeObservers(RunningChanged, running, "running");
            }
        }
        finally { DrainDeferredNative(); }
    }

    private void RunNative(Work work)
    {
        foreach (var operation in work.Cancels)
        {
            try
            {
                var status = operation.Cancel();
                if (status != 0 && !operation.IsSettled) Report(L.Format("DNS cancellation failed: {0}. Native resources remain safely owned; restarting the app is the final recovery if completion cannot be established.", new Win32Exception((int)status).Message));
            }
            catch (Exception error) when (IsOperationFailure(error))
            {
                Report(L.Format("DNS cancellation failed: {0}. Native resources remain safely owned; restarting the app is the final recovery if completion cannot be established.", error.Message));
            }
        }
        foreach (var owner in work.Starts)
        {
            Work? abandoned = null;
            lock (_sync)
            {
                if (_disposed || owner.Retired || owner.Epoch != _epoch || owner.Operation.IsSettled)
                {
                    abandoned = new(); RetireLocked(owner, abandoned);
                }
            }
            if (abandoned is not null) { Run(abandoned); continue; }
            uint status;
            try { status = owner.Operation.Start(); }
            catch (Exception error) when (IsOperationFailure(error))
            {
                StartFailed(owner); Report(L.Format("Receiver discovery failed: {0}. You can add a receiver manually.", error.Message)); continue;
            }
            var followup = new Work();
            lock (_sync)
            {
                // Inline completion/retirement wins over Start's return value. Adapter
                // settlement before a failed Start returns has already applied backoff;
                // report its status without retiring or incrementing failures twice.
                if (owner.Retired || _disposed || owner.Epoch != _epoch)
                {
                    RetireLocked(owner, followup);
                    if (status != PendingStatus && owner.UnexpectedSettlement && !_disposed && owner.Epoch == _epoch)
                        followup.Errors.Add(new(L.Format("DNS request did not remain pending: {0}. Discovery will retry with backoff.", status)));
                }
                else if (status != PendingStatus)
                {
                    if (owner.Resolve) FailResolveLocked(owner, Now, followup, DiscoveryTraceReason.QueryStartFailed);
                    else FailBrowseLocked(owner, Now, followup, DiscoveryTraceReason.QueryStartFailed);
                    followup.Errors.Add(new(L.Format("DNS request did not remain pending: {0}. Discovery will retry with backoff.", status)));
                }
            }
            Run(followup);
        }
    }

    private void StartFailed(Owner owner)
    {
        var work = new Work();
        lock (_sync)
        {
            if (owner.Retired || _disposed || owner.Epoch != _epoch) RetireLocked(owner, work);
            else if (owner.Resolve) FailResolveLocked(owner, Now, work, DiscoveryTraceReason.QueryStartFailed);
            else FailBrowseLocked(owner, Now, work, DiscoveryTraceReason.QueryStartFailed);
        }
        Run(work);
    }

    private static bool IsOperationFailure(Exception error) => error is Win32Exception or InvalidOperationException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private void Report(string message) => Report(new DiscoveryFailure(message));

    private void Report(DiscoveryFailure failure)
    {
        try
        {
            lock (_publication)
            {
                lock (_sync) { if (_disposed) return; }
                Log(failure.Message);
                bool superseded;
                lock (_sync)
                {
                    if (_disposed) return;
                    superseded = failure.Attempt is { } attempt && (attempt.Epoch != _epoch
                        || _records.TryGetValue(attempt.Instance, out var cached) && cached.ResolveSequence > attempt.Sequence
                            && cached.Record.ServiceType.Equals(attempt.ServiceType, StringComparison.OrdinalIgnoreCase));
                }
                if (!superseded) InvokeObservers(Failed, failure, "failure");
            }
        }
        finally { DrainDeferredNative(); }
    }

    private void PublishResolution(DiscoveryResolveAttempt resolution)
    {
        try
        {
            lock (_publication)
            {
                lock (_sync)
                {
                    if (_disposed || resolution.Epoch != _epoch || !_records.TryGetValue(resolution.Instance, out var cached)
                        || cached.ResolveSequence != resolution.Sequence || cached.Expires <= Now
                        || !cached.Record.ServiceType.Equals(resolution.ServiceType, StringComparison.OrdinalIgnoreCase)) return;
                }
                InvokeObservers(ResolveSucceeded, resolution, "resolve success");
            }
        }
        finally { DrainDeferredNative(); }
    }

    private void Publish()
    {
        try
        {
            lock (_publication)
            {
                IReadOnlyList<Receiver> receivers; string[] messages;
                lock (_sync)
                {
                    if (_disposed) return;
                    var diagnostics = new HashSet<string>(StringComparer.Ordinal);
                    receivers = ReceiverAggregator.Build(_records.Values.Select(record => record.Record), message => diagnostics.Add(message));
                    messages = diagnostics.Except(_diagnostics).ToArray(); _diagnostics = diagnostics;
                }
                foreach (var message in messages) Log(message);
                InvokeObservers(Changed, receivers, "catalog");
            }
        }
        finally { DrainDeferredNative(); }
    }

    private void Log(string message)
    {
        try { _log(message); }
        catch (Exception error) { TraceObserverFailure("log", error); }
    }

    private static void InvokeObservers<T>(Action<T>? observers, T value, string channel)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
            try { observer(value); }
            catch (Exception error) { TraceObserverFailure(channel, error); }
    }

    private static void TraceObserverFailure(string channel, Exception error)
    {
        try { System.Diagnostics.Trace.TraceError("Discovery {0} observer: {1}", channel, error.Message); }
        catch { } // A failing fallback sink must not abandon publication or native work.
    }

    private static bool Equal(ServiceRecord left, ServiceRecord right) =>
        left.Instance == right.Instance && left.ServiceType == right.ServiceType && left.Address == right.Address && left.Port == right.Port
        && left.Txt.Count == right.Txt.Count && left.Txt.All(pair => right.Txt.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static string Describe(ServiceRecord record) => System.Text.Json.JsonSerializer.Serialize(new
    {
        record.Instance, record.ServiceType, record.Address, record.Port,
        DeviceId = record.Txt.GetValueOrDefault("deviceid", ""),
        Model = record.Txt.GetValueOrDefault("model", record.Txt.GetValueOrDefault("am", "")),
        StereoId = record.Txt.GetValueOrDefault("tsid", "")
    });

    public void Dispose()
    {
        var work = new Work(); ITimer? timer;
        lock (_publication)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true; ++_epoch; ++_restartSequence; timer = _timer; _timer = null;
                foreach (var owner in _owners.Values) RetireLocked(owner, work);
                _pending.Clear(); _advertised.Clear(); _records.Clear();
                TraceLocked(DiscoveryTraceEvent.Disposed);
                foreach (var browser in _browsers.Values) browser.Owner = null;
                if (_enableInfrastructure)
                {
                    NetworkChange.NetworkAddressChanged -= NetworkChanged;
                    NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                }
            }
        }
        timer?.Dispose(); Run(work);
    }

    private sealed class Advertisement(string name, string type, TimeSpan expires, TimeSpan nextAttempt)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public TimeSpan Expires { get; set; } = expires;
        public TimeSpan NextAttempt { get; set; } = nextAttempt;
        public TimeSpan? ConcurrencyDeferredSince { get; set; }
        public int Failures { get; set; }
    }
    private sealed record CachedRecord(ServiceRecord Record, TimeSpan Expires, long ResolveSequence, bool ResumeGrace = false);
    private sealed class Browser(string type)
    {
        public string Type { get; } = type;
        public Owner? Owner { get; set; }
        public TimeSpan NextAttempt { get; set; }
        public int Failures { get; set; }
    }
    private sealed class Owner(IDnsServiceOperation operation, string name, string type, long epoch, bool resolve)
    {
        public IDnsServiceOperation Operation { get; } = operation;
        public string Name { get; } = name;
        public string Type { get; } = type;
        public long Epoch { get; } = epoch;
        public bool Resolve { get; } = resolve;
        public TimeSpan Deadline { get; init; }
        public long ResolveSequence { get; init; }
        public bool Retired { get; set; }
        public bool HasResult { get; set; }
        public bool UnexpectedSettlement { get; set; }
        public bool CancelRequested { get; set; }
        public Action? SettledHandler { get; set; }
        public Action<string>? CancellationFailedHandler { get; set; }
        public Action<string>? CallbackFailedHandler { get; set; }
    }
    private sealed class Work
    {
        public List<Owner> Starts { get; } = [];
        public List<IDnsServiceOperation> Cancels { get; } = [];
        public List<string> Logs { get; } = [];
        public List<DiscoveryFailure> Errors { get; } = [];
        public List<DiscoveryResolveAttempt> Resolutions { get; } = [];
        public bool Publish { get; set; }
    }
}
