using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

[Collection("Native DNS ownership")]
public sealed class ResolveProcessTests
{
    private const string Type = "_airplay._tcp.local", Name = "Synthetic._airplay._tcp.local";
    private static readonly ResolveRequest Request = new(Name, Type, 42);
    private static string Reply(string address = "192.0.2.1") => JsonSerializer.Serialize(new ResolveReply(1, 0,
        new(Name, Type, address, 7000, new Dictionary<string, string> { ["deviceid"] = "synthetic" })));

    [Fact]
    public async Task CopiedResultDoesNotSettleBeforeObservedExit()
    {
        using var fixture = new Fixture();
        var child = fixture.Factory.Next;
        var operation = fixture.Create();
        operation.Start(); await child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Reply.SetResult(Reply()); await fixture.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await child.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Results); Assert.Equal("192.0.2.1", fixture.Record?.Address);
        Assert.False(operation.IsSettled); Assert.False(child.Disposed);
        Assert.Equal(fixture.Baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        operation.Cancel(); operation.Cancel(); Assert.False(operation.IsSettled);
        child.Exit.SetResult(); await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(operation.IsSettled); Assert.True(child.Disposed);
        Assert.Equal(fixture.Baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
    }
    [Fact]
    public async Task CancellationWhileStartIsBlockedRetainsTheExactObject()
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next;
        using var release = new ManualResetEventSlim(); child.StartGate = release;
        var operation = fixture.Create(); operation.Start();
        try
        {
            await child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Cancel(); Assert.False(operation.IsSettled); Assert.False(child.Disposed);
            Assert.Equal(fixture.Baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        }
        finally { release.Set(); }
        await child.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Reply.TrySetResult(Reply()); Assert.Equal(0, fixture.Results);
        child.Exit.SetResult(); await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Results); Assert.True(child.Disposed);
    }
    [Fact]
    public void CancellationBeforeStartProvesNoProcessWasAssociated()
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next; var operation = fixture.Create();
        operation.Cancel(); Assert.True(operation.IsSettled); Assert.Equal(1223u, operation.Start());
        Assert.Equal(0, child.Starts); Assert.True(child.Disposed);
        Assert.Equal(fixture.Baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
    }
    [Fact]
    public async Task FixtureCleanupJoinsSettlementAfterFlagPrecedesChildDisposal()
    {
        var fixture = new Fixture(); var child = fixture.Factory.Next;
        using var releaseDispose = new ManualResetEventSlim(); child.DisposeGate = releaseDispose;
        var operation = fixture.Create(); var cancellation = Task.Run(operation.Cancel);
        Task? cleanup = null;
        try
        {
            await child.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(operation.IsSettled);
            Assert.False(child.Disposed);
            Assert.False(fixture.Settled.Task.IsCompleted);
            Assert.Equal(fixture.Baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            cleanup = Task.Factory.StartNew(fixture.Dispose, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await fixture.SettlementJoinEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleanup.IsCompleted);
            Assert.False(fixture.Settled.Task.IsCompleted);
            Assert.Equal(fixture.Baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            releaseDispose.Set();
            await Task.WhenAll(cancellation, cleanup).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Settled.Task.IsCompleted);
            Assert.True(child.Disposed);
            Assert.Equal(1, child.Disposals);
            Assert.Equal(1, fixture.Settlements);
            Assert.Equal(0, child.Starts);
            Assert.Equal(fixture.Baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        }
        finally
        {
            releaseDispose.Set();
            try { await Task.WhenAll(cancellation, cleanup ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Dispose();
            }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingStartRetainsAnAssociatedProcessUntilExit(bool associated)
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next;
        child.StartFailure = true; child.AssociateBeforeFailure = associated;
        var operation = fixture.Create(); operation.Start(); await fixture.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1u, fixture.Status); Assert.NotEmpty(fixture.Errors);
        if (associated)
        {
            await child.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsSettled); Assert.False(child.Disposed); child.Exit.SetResult();
        }
        await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.Disposed);
    }
    [Fact]
    public async Task FailedKillKeepsOwnerUntilLateSuccessfulExitObservation()
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next; child.KillFailure = true;
        var operation = fixture.Create(); operation.Start(); await child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); operation.Cancel();
        await fixture.CancellationError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsSettled); Assert.False(child.Disposed); Assert.Equal(1, child.Kills);
        child.Reply.TrySetException(new IOException("synthetic broken pipe"));
        Assert.Empty(fixture.Errors); // Cancellation does not publish a new resolve failure.
        child.Exit.SetResult(); await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.Disposed);
    }
    [Fact]
    public async Task FailedExitWaitCannotReleaseOwnerAndLaterObservationRecoversIt()
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next; child.FailFirstWait = true;
        var operation = fixture.Create(); operation.Start(); await fixture.CancellationError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsSettled); Assert.False(child.Disposed);
        Assert.Equal(fixture.Baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        await child.WaitRetried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsSettled); child.Exit.SetResult();
        await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.Disposed);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    [InlineData("{\"Version\":2,\"Status\":1,\"Record\":null}")]
    public async Task MissingOrInvalidReplyFailsAndStillRequiresExit(string? reply)
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next; var operation = fixture.Create();
        operation.Start(); await child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); child.Reply.SetResult(reply);
        await fixture.Result.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1u, fixture.Status);
        await child.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(operation.IsSettled);
        child.Exit.SetResult(); await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task ExitBeforeBufferedReplyDoesNotDiscardTheRecord()
    {
        using var fixture = new Fixture(); var child = fixture.Factory.Next;
        child.Exit.SetResult(); child.Reply.SetResult(Reply()); var operation = fixture.Create(); operation.Start();
        await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, fixture.Results); Assert.Equal(0u, fixture.Status);
    }
    [Fact]
    public async Task GlobalCapacityIncludesCancelledOwnersAcrossApiInstances()
    {
        var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        var fixtures = new List<Fixture>();
        try
        {
            for (var i = baseline; i < ResolveProcessDnsServiceApi.ProcessLimit; ++i)
            {
                var fixture = new Fixture(); fixtures.Add(fixture); var operation = fixture.Create(); operation.Start();
                await fixture.Factory.Next.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); operation.Cancel();
                await fixture.Factory.Next.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            using var extra = new Fixture();
            var capacity = Assert.Throws<DnsServiceCapacityException>(() => extra.Create());
            Assert.Equal(DnsServiceCapacityReason.ResolveConcurrency, capacity.Reason);
            Assert.All(fixtures, fixture => Assert.False(fixture.Operation!.IsSettled));
            fixtures[0].Factory.Next.Exit.SetResult(); await fixtures[0].Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var resumed = extra.Create(); Assert.Equal(baseline + fixtures.Count, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            resumed.Cancel(); Assert.True(resumed.IsSettled);
        }
        finally
        {
            foreach (var fixture in fixtures) fixture.Factory.Next.Exit.TrySetResult();
            foreach (var fixture in fixtures) { await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); fixture.Dispose(); }
        }
        Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
    }
    [Fact]
    public async Task OneHundredFiniteAttemptsDoNotExhaustPerNameOrGlobalOwnership()
    {
        var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        var tokens = new HashSet<long>();
        for (var i = 0; i < 100; ++i)
        {
            using var fixture = new Fixture(); var operation = fixture.Create(); Assert.True(tokens.Add(operation.Token));
            fixture.Factory.Next.Reply.SetResult(Reply()); fixture.Factory.Next.Exit.SetResult(); operation.Start();
            await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, fixture.Results);
            Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        }
    }
    [Fact]
    public async Task OneHundredScopedDiscoveryCyclesPublishAndRecoverAllHelperSlots()
    {
        var factory = new SequenceFactory(); var browsers = new Browsers();
        var api = new TrackingApi(new ResolveProcessDnsServiceApi(browsers, factory));
        using var discovery = new WindowsDiscovery(api, TimeProvider.System,
            () => [new("synthetic", "LAN", "LAN", "192.0.2.254", true, 42)], _ => { });
        discovery.SetInterface("synthetic");
        var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        TaskCompletionSource? published = null; string? expectedAddress = null;
        discovery.Changed += rows => { if (rows.Count == 1 && rows[0].Address == expectedAddress) published?.TrySetResult(); };
        try
        {
            for (var i = 0; i < 100; ++i)
            {
                expectedAddress = $"192.0.2.{i + 1}";
                published = new(TaskCreationOptions.RunContinuationsAsynchronously);
                discovery.Start(); browsers.Latest[0].Announce();
                var child = factory.Children[^1]; await child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                api.LatestResolve!.Settled += () => exited.TrySetResult();
                child.Reply.SetResult(Reply(expectedAddress)); await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, discovery.Snapshot.PendingResolves);
                Assert.False(api.LatestResolve.IsSettled);
                discovery.Stop(); await child.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, discovery.Snapshot.ActiveQueries); Assert.Equal(1, discovery.Snapshot.NativeOwners);
                child.Exit.SetResult(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, discovery.Snapshot.NativeOwners);
                Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            }
            Assert.Equal(100, factory.Children.Count);
            Assert.All(browsers.All, browser => { Assert.Equal(42u, browser.Index); Assert.Equal(1, browser.Cancellations); });
        }
        finally
        {
            discovery.Stop(); foreach (var child in factory.Children) { child.Exit.TrySetResult(); child.Reply.TrySetResult(null); }
            await Task.WhenAll(api.Settlements).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact]
    public async Task HealthyResolveConcurrencyDefersExtraAdvertisementsWithoutOwnershipWarning()
    {
        var factory = new BatchFactory(); var browsers = new Browsers();
        var api = new TrackingApi(new ResolveProcessDnsServiceApi(browsers, factory));
        using var discovery = new WindowsDiscovery(api, new ManualClock(),
            () => [new("synthetic", "LAN", "LAN", "192.0.2.254", true, 42)], _ => { });
        discovery.SetInterface("synthetic");
        var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        var errors = new ConcurrentQueue<string>();
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allPublished = new TaskCompletionSource<IReadOnlyList<Receiver>>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Failed += errors.Enqueue;
        discovery.Changed += rows =>
        {
            if (rows.Count == 8) firstBatch.TrySetResult();
            if (rows.Count == 9) allPublished.TrySetResult(rows);
        };
        var names = Enumerable.Range(0, 9).Select(index => $"Synthetic{index}.{Type}").ToArray();
        try
        {
            discovery.Start(); browsers.Latest[0].Announce(names);
            Assert.Equal(8, ResolveProcessDnsServiceApi.ProcessLimit);
            Assert.Equal(8, factory.Children.Count);
            Assert.Equal(8, discovery.Snapshot.PendingResolves);
            Assert.Equal(baseline + 8, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            var firstSettled = api.Resolves.Select(SettlementAfterCoordinator).ToArray();
            await Task.WhenAll(factory.Children.Select(item => item.Child.StartEntered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(errors); // A ninth healthy advertisement is ordinary queueing, not unsafe native ownership.

            for (var index = 0; index < 8; ++index) factory.Complete(index);
            await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(firstSettled).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            Assert.Equal(2, discovery.Snapshot.NativeOwners); // Only the two synthetic browsers remain.

            discovery.Tick(); // Deterministic maintenance admits the still-advertised ninth name.
            Assert.Equal(9, factory.Children.Count);
            Assert.Equal(1, discovery.Snapshot.PendingResolves);
            Assert.Equal(baseline + 1, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            var lastSettled = SettlementAfterCoordinator(api.LatestResolve!);
            await factory.Children[8].Child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            factory.Complete(8);
            var rows = await allPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await lastSettled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(names.Order(), rows.Select(row => row.Name + "." + Type).Order());
            Assert.Equal(9, rows.Select(row => row.DeviceId).Distinct().Count());
            Assert.Equal(0, discovery.Snapshot.PendingResolves);
            Assert.Empty(errors);
        }
        finally
        {
            discovery.Stop();
            foreach (var item in factory.Children) { item.Child.Reply.TrySetResult(null); item.Child.Exit.TrySetResult(); }
            await Task.WhenAll(api.Settlements).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, discovery.Snapshot.NativeOwners);
        Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        Assert.All(factory.Children, item => { Assert.True(item.Child.Disposed); Assert.Equal(1, item.Child.Disposals); });
    }
    [Fact]
    public async Task RetainedHelperBatchReportsProlongedDeferralAndOneObservedExitAdmitsWaitingName()
    {
        var fixtures = new List<Fixture>();
        var factory = new BatchFactory(); var browsers = new Browsers(); var clock = new ManualClock();
        var api = new TrackingApi(new ResolveProcessDnsServiceApi(browsers, factory));
        using var discovery = new WindowsDiscovery(api, clock,
            () => [new("synthetic", "LAN", "LAN", "192.0.2.254", true, 42)], _ => { });
        discovery.SetInterface("synthetic");
        var baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        var errors = new ConcurrentQueue<string>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Failed += errors.Enqueue;
        discovery.Changed += rows => { if (rows.Count == 1) published.TrySetResult(); };
        try
        {
            Assert.Equal(0, baseline);
            for (var index = 0; index < 8; ++index)
            {
                var fixture = new Fixture(); fixtures.Add(fixture);
                var operation = fixture.Create(); operation.Start();
                await fixture.Factory.Next.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                operation.Cancel(); await fixture.Factory.Next.KillEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            discovery.Start(); browsers.Latest[0].Announce(Name);
            Assert.Empty(factory.Children); Assert.Empty(errors);
            Assert.Equal(8, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            Assert.All(fixtures, fixture => Assert.False(fixture.Operation!.IsSettled));

            clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)); discovery.Tick();
            browsers.Latest[0].Announce(Name); // Repeated PTRs cannot postpone the deferral notice.
            Assert.Empty(errors);
            clock.Advance(TimeSpan.FromTicks(1)); discovery.Tick(); discovery.Tick();
            Assert.Equal(L.Get("Receiver discovery is waiting for earlier DNS queries to finish. It will retry automatically; restart AirFlash if it remains paused."), Assert.Single(errors));
            Assert.Empty(factory.Children);
            Assert.Equal(0, discovery.Snapshot.PendingResolves);
            Assert.Equal(8, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            Assert.All(fixtures, fixture => { Assert.False(fixture.Operation!.IsSettled); Assert.False(fixture.Factory.Next.Disposed); });

            browsers.Latest[0].Withdraw(Name); browsers.Latest[0].Announce(Name);
            discovery.Tick(); Assert.Single(errors); // Removal starts a new continuous wait, not an immediate repeat.
            clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)); discovery.Tick(); Assert.Single(errors);
            clock.Advance(TimeSpan.FromTicks(1)); discovery.Tick(); Assert.Equal(2, errors.Count);

            fixtures[0].Factory.Next.Exit.SetResult();
            await fixtures[0].Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            discovery.Tick();
            Assert.Single(factory.Children); Assert.Equal(1, discovery.Snapshot.PendingResolves);
            Assert.Equal(8, ResolveProcessDnsServiceApi.ProcessOwnerCount);
            var settled = SettlementAfterCoordinator(api.LatestResolve!);
            await factory.Children[0].Child.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            factory.Complete(0);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await settled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, errors.Count); Assert.Equal(0, discovery.Snapshot.PendingResolves);
            Assert.Equal(7, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        }
        finally
        {
            discovery.Stop();
            foreach (var item in factory.Children) { item.Child.Reply.TrySetResult(null); item.Child.Exit.TrySetResult(); }
            foreach (var fixture in fixtures) fixture.Factory.Next.Exit.TrySetResult();
            await Task.WhenAll(api.Settlements.Concat(fixtures.Select(fixture => fixture.Settled.Task))).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var fixture in fixtures) fixture.Dispose();
        }
        Assert.Equal(baseline, ResolveProcessDnsServiceApi.ProcessOwnerCount);
        Assert.Equal(0, discovery.Snapshot.NativeOwners);
    }
    private static Task SettlementAfterCoordinator(IDnsServiceOperation operation)
    {
        // Register after WindowsDiscovery so this joins its owner removal as well
        // as child exit/disposal. The child cannot exit until the fixture permits it.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Settled += () => settled.TrySetResult();
        return settled.Task;
    }
    [Fact]
    public async Task WireRejectsOversizeTruncatedAndInvalidUtf8Output()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveWire.ReadLineAsync(new MemoryStream(new byte[ResolveWire.MaximumReplyBytes + 1])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveWire.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes("partial"))));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => ResolveWire.ReadLineAsync(new MemoryStream([255, 10])));
        var accepted = await ResolveWire.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(Reply() + "\nignored second result\n")));
        Assert.Equal("192.0.2.1", ResolveWire.Parse(accepted, Request).Record?.Address);
        Assert.Throws<InvalidOperationException>(() => ResolveWire.Parse(Reply("::1"), Request));
    }
    [Fact]
    public async Task ExactReplyByteLimitExcludesLfAndHasAValidAcceptanceControl()
    {
        var prefix = Reply()[..^1] + ",\"Padding\":\"";
        const string suffix = "\"}";
        var padding = ResolveWire.MaximumReplyBytes - Encoding.UTF8.GetByteCount(prefix + suffix);
        var exact = prefix + new string('x', padding) + suffix;
        Assert.Equal(ResolveWire.MaximumReplyBytes, Encoding.UTF8.GetByteCount(exact));
        var accepted = await ResolveWire.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(exact + "\n")));
        Assert.NotNull(ResolveWire.Parse(accepted, Request).Record);
        var exceeded = prefix + new string('x', padding + 1) + suffix;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveWire.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(exceeded + "\n"))));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.Parse(exceeded, Request));
    }
    [Fact]
    public void WireRejectsInvalidRecordIdentityPortTxtAndRequest()
    {
        var valid = JsonSerializer.Deserialize<ResolveReply>(Reply())!;
        foreach (var record in new[]
        {
            valid.Record! with { Instance = "Other._airplay._tcp.local" },
            valid.Record! with { ServiceType = "_raop._tcp.local" },
            valid.Record! with { Instance = null! },
            valid.Record! with { Port = 0 },
            valid.Record! with { Port = 65536 },
            valid.Record! with { Txt = null! },
            valid.Record! with { Txt = new Dictionary<string, string> { ["key"] = new('x', 4097) } }
        }) Assert.Throws<InvalidOperationException>(() => ResolveWire.Parse(JsonSerializer.Serialize(valid with { Record = record }), Request));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.ValidateRequest(Request with { Name = "" }));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.ValidateRequest(Request with { Name = new('x', 1025) }));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.ValidateRequest(Request with { Name = "invalid\0name" }));
        Assert.Throws<InvalidOperationException>(() => ResolveWire.ValidateRequest(Request with { Type = "_http._tcp.local" }));
        Assert.Equal("4294967295", ResolveChildFactory.StartInfo("AirFlash.exe", "", Request with { InterfaceIndex = uint.MaxValue }).ArgumentList[1]);
    }
    [Fact]
    public void HostSelectionForwardsActualAppAssemblyOnlyForDotnetAndPreservesInterface()
    {
        var apphost = ResolveChildFactory.StartInfo("AirFlash.exe", "AirFlash.dll", Request);
        Assert.Equal(new[] { "--dns-resolve-helper", "42", Type, Name }, apphost.ArgumentList);
        Assert.True(apphost.CreateNoWindow); Assert.False(apphost.UseShellExecute);
        var dotnet = ResolveChildFactory.StartInfo("dotnet.exe", "AirFlash.dll", Request);
        Assert.Equal("AirFlash.dll", dotnet.ArgumentList[0]);
        Assert.Throws<InvalidOperationException>(() => ResolveChildFactory.StartInfo("dotnet.exe", "", Request));
        Assert.Throws<InvalidOperationException>(() => ResolveChildFactory.ForApplication()); // Never launch the fixture host.
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Factory Factory = new();
        public readonly int Baseline = ResolveProcessDnsServiceApi.ProcessOwnerCount;
        public readonly TaskCompletionSource Result = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously), CancellationError = new(TaskCreationOptions.RunContinuationsAsynchronously), SettlementJoinEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Errors = [];
        public IDnsServiceOperation? Operation;
        public int Results, Settlements;
        public uint Status;
        public ServiceRecord? Record;
        public IDnsServiceOperation Create()
        {
            Operation = new ResolveProcessDnsServiceApi(new NoBrowse(), Factory).CreateResolve(Name, Type, 42, (status, record) =>
            { Status = status; Record = record; Interlocked.Increment(ref Results); Result.TrySetResult(); });
            Operation.Settled += () => { Interlocked.Increment(ref Settlements); Settled.TrySetResult(); };
            Operation.CallbackFailed += error => Errors.Add(error);
            Operation.CancellationFailed += _ => CancellationError.TrySetResult();
            return Operation;
        }
        public void Dispose()
        {
            Operation?.Cancel(); Factory.Next.StartGate?.Set(); Factory.Next.Exit.TrySetResult(); Factory.Next.Reply.TrySetResult(null);
            if (Operation is not null)
            {
                // The flag is published before child disposal and owner removal.
                // Only the registered notification joins that cleanup boundary.
                SettlementJoinEntered.TrySetResult();
                Assert.True(Settled.Task.Wait(TimeSpan.FromSeconds(5)), "Synthetic worker was not joined.");
            }
        }
    }
    private sealed class NoBrowse : IDnsServiceApi
    {
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback) => throw new NotSupportedException();
        public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback) => throw new NotSupportedException();
    }
    private sealed class Factory : IResolveChildFactory
    {
        public readonly Child Next = new();
        public IResolveChild Create(ResolveRequest request) { Assert.Equal(Request, request); return Next; }
    }
    private sealed class SequenceFactory : IResolveChildFactory
    {
        public readonly List<Child> Children = [];
        public IResolveChild Create(ResolveRequest request) { Assert.Equal(Request, request); var child = new Child(); Children.Add(child); return child; }
    }
    private sealed class BatchFactory : IResolveChildFactory
    {
        public readonly List<(ResolveRequest Request, Child Child)> Children = [];
        public IResolveChild Create(ResolveRequest request)
        {
            Assert.Equal(Type, request.Type); Assert.Equal(42u, request.InterfaceIndex);
            var child = new Child(); Children.Add((request, child)); return child;
        }
        public void Complete(int index)
        {
            var (request, child) = Children[index];
            child.Reply.SetResult(JsonSerializer.Serialize(new ResolveReply(1, 0,
                new(request.Name, request.Type, $"192.0.2.{index + 1}", 7000,
                    new Dictionary<string, string> { ["deviceid"] = $"synthetic-{index}" }))));
            child.Exit.SetResult();
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
    private sealed class TrackingApi(IDnsServiceApi inner) : IDnsServiceApi
    {
        public IDnsServiceOperation? LatestResolve;
        public readonly List<IDnsServiceOperation> Resolves = [];
        public readonly List<Task> Settlements = [];
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback) => inner.CreateBrowse(type, index, callback);
        public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback)
        {
            LatestResolve = inner.CreateResolve(name, type, index, callback);
            Resolves.Add(LatestResolve);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LatestResolve.Settled += () => settled.TrySetResult(); Settlements.Add(settled.Task);
            return LatestResolve;
        }
    }
    private sealed class Browsers : IDnsServiceApi
    {
        private long _nextToken;
        public readonly List<Browser> All = [];
        public Browser[] Latest => All.TakeLast(2).ToArray();
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback)
        { var operation = new Browser(--_nextToken, type, index, callback); All.Add(operation); return operation; }
        public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback) => throw new NotSupportedException();
        public sealed class Browser(long token, string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback) : IDnsServiceOperation
        {
            public long Token => token;
            public string Name => type;
            public uint Index => index;
            public bool IsResolve => false;
            public bool IsSettled { get; private set; }
            public int Cancellations;
            public event Action? Settled;
            public event Action<string>? CancellationFailed { add { } remove { } }
            public event Action<string>? CallbackFailed { add { } remove { } }
            public uint Start() => 9506;
            public uint Cancel() { if (!IsSettled) { Cancellations++; IsSettled = true; Settled?.Invoke(); } return 0; }
            public void Announce(params string[] names) => callback(0,
                (names.Length == 0 ? [ResolveProcessTests.Name] : names).Select(name => new DnsAnnouncement(name, 120)).ToArray());
            public void Withdraw(string name) => callback(0, [new(name, 0)]);
        }
    }
    private sealed class Child : IResolveChild
    {
        public readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), KillEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), Exit = new(TaskCreationOptions.RunContinuationsAsynchronously), WaitRetried = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string?> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; private set; }
        public bool Disposed, StartFailure, AssociateBeforeFailure, KillFailure, FailFirstWait;
        public int Starts, Kills, Waits, Disposals;
        public ManualResetEventSlim? StartGate, DisposeGate;
        public void Start()
        {
            Starts++; StartEntered.TrySetResult();
            if (StartGate is not null && !StartGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic start gate was not released.");
            Started = !StartFailure || AssociateBeforeFailure;
            if (StartFailure) throw new InvalidOperationException("Synthetic start failure.");
        }
        public Task<string?> ReadReplyAsync() => Reply.Task;
        public Task WaitForExitAsync()
        {
            if (++Waits == 1 && FailFirstWait) return Task.FromException(new IOException("Synthetic exit wait failure."));
            WaitRetried.TrySetResult(); return Exit.Task;
        }
        public void Kill() { Kills++; KillEntered.TrySetResult(); if (KillFailure) throw new InvalidOperationException("Synthetic kill failure."); }
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            if (DisposeGate is not null && !DisposeGate.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Synthetic child disposal gate was not released.");
            Disposed = true;
        }
    }
}
