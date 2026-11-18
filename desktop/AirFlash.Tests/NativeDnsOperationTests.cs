using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

[CollectionDefinition("Native DNS ownership", DisableParallelization = true)]
public sealed class NativeDnsOwnershipCollection { }

// This suite executes the production unmanaged owner with actual HGlobal inputs,
// but every DNS call and result release is injected; dnsapi.dll is never called.
[Collection("Native DNS ownership")]
public sealed class NativeDnsOperationTests
{
    private const string Type = "_airplay._tcp.local";
    private const string Name = "Office._airplay._tcp.local";

    [Fact]
    public void DocumentedBrowseCancellationStatusSettlesOwnerAndReleasesInputs()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => { });
        var settled = 0;
        operation.Settled += () => settled++;
        operation.Start(); operation.Cancel();
        // DnsServiceBrowseCancel documents ERROR_CANCELLED, which is 1223,
        // rather than ERROR_OPERATION_ABORTED (995).
        NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
        Assert.True(operation.IsSettled);
        Assert.Equal(1, settled);
        before.AssertRestored(released: 1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData(995u)]
    public void OtherBrowseStatusesCannotSettleCanceledOwnerBeforeDocumentedTerminalCallback(uint status)
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var callbacks = 0;
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => callbacks++);
        operation.Start(); operation.Cancel();
        NativeDnsServiceApi.DispatchBrowse(status, new(operation.Token), calls.PtrRecord(Name, 90), calls);
        Assert.False(operation.IsSettled);
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, calls.FreeRecordCalls);
        NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void DocumentedBrowseTerminalCallbackDuringCancelRetainsInputsUntilNativeCancelReturns()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => { });
        operation.Start();
        var heldDuringFree = false;
        var heldAfterCallback = false;
        calls.BeforeFree = _ => heldDuringFree = !operation.IsSettled && NativeDnsServiceApi.AllocatedBufferCount == before.Buffers + 3;
        calls.DuringCancel = call =>
        {
            NativeDnsServiceApi.DispatchBrowse(1223, call.Context, calls.PtrRecord(Name, 90), calls);
            heldAfterCallback = !operation.IsSettled && calls.ReadRequestName(call.Request) == Type
                && Marshal.ReadIntPtr(call.Cancel) == call.Context;
        };
        operation.Cancel();
        Assert.True(heldDuringFree);
        Assert.True(heldAfterCallback);
        Assert.Equal(1, calls.FreeRecordCalls);
        Assert.True(operation.IsSettled);
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void NativeRequestAndInstanceLayoutsMatchX64DnsServiceAbi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Marshal.SizeOf<NativeDnsServiceApi.NativeServiceRequest>());
        Assert.Equal(new IntPtr(16), Marshal.OffsetOf<NativeDnsServiceApi.NativeServiceRequest>(nameof(NativeDnsServiceApi.NativeServiceRequest.Callback)));
        Assert.Equal(new IntPtr(24), Marshal.OffsetOf<NativeDnsServiceApi.NativeServiceRequest>(nameof(NativeDnsServiceApi.NativeServiceRequest.Context)));
        Assert.Equal(72, Marshal.SizeOf<NativeDnsServiceApi.NativeServiceInstance>());
        Assert.Equal(new IntPtr(40), Marshal.OffsetOf<NativeDnsServiceApi.NativeServiceInstance>(nameof(NativeDnsServiceApi.NativeServiceInstance.Count)));
        Assert.Equal(new IntPtr(48), Marshal.OffsetOf<NativeDnsServiceApi.NativeServiceInstance>(nameof(NativeDnsServiceApi.NativeServiceInstance.Keys)));
        Assert.Equal(new IntPtr(64), Marshal.OffsetOf<NativeDnsServiceApi.NativeServiceInstance>(nameof(NativeDnsServiceApi.NativeServiceInstance.InterfaceIndex)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void NonPendingStartSettlesOnceAndReleasesAllInputBuffers(uint status)
    {
        using var calls = new FakeNativeCalls { StartStatus = status };
        var api = new NativeDnsServiceApi(calls);
        var before = Counters.Read();
        var operation = api.CreateResolve(Name, Type, 17, (_, _) => { });
        var settled = 0;
        operation.Settled += () => settled++;
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        Assert.Equal(status, operation.Start());
        Assert.True(operation.IsSettled);
        Assert.Equal(1, settled);
        operation.Cancel(); operation.Start();
        Assert.Equal(1, calls.StartCalls);
        Assert.Equal(0, calls.CancelCalls);
        Assert.Equal(1, settled);
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void CancelBeforeStartSettlesWithoutInvokingNativeApi()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => { });
        var settled = 0;
        operation.Settled += () => settled++;
        Assert.Equal(0u, operation.Cancel());
        Assert.Equal(1223u, operation.Start());
        operation.Cancel();
        Assert.Equal(1, settled);
        Assert.Equal(0, calls.StartCalls);
        Assert.Equal(0, calls.CancelCalls);
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void LoaderFailureSettlesButAmbiguousNativeBoundaryExceptionRetainsInputs()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var api = new NativeDnsServiceApi(calls);
        calls.StartError = new DllNotFoundException("Fixture loader failure.");
        var loaderFailure = api.CreateResolve(Name, Type, 17, (_, _) => { });
        Assert.Throws<DllNotFoundException>(() => loaderFailure.Start());
        Assert.True(loaderFailure.IsSettled);
        Assert.Equal(before.Buffers, NativeDnsServiceApi.AllocatedBufferCount);
        calls.StartError = new InvalidOperationException("Fixture error after an ambiguous native boundary.");
        var ambiguous = api.CreateResolve(Name, Type, 17, (_, _) => { });
        Assert.Throws<InvalidOperationException>(() => ambiguous.Start());
        Assert.False(ambiguous.IsSettled);
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        ambiguous.Cancel();
        Assert.False(ambiguous.IsSettled);
        NativeDnsServiceApi.DispatchResolve(995, new(ambiguous.Token), IntPtr.Zero, calls);
        Assert.False(ambiguous.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 2);
    }

    [Fact]
    public void CancellationExceptionAndResultRetainInputsUntilFakeBoundaryEnds()
    {
        using var calls = new FakeNativeCalls { CancelError = new InvalidOperationException("Fixture cancel boundary failure.") };
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => { });
        operation.Start();
        Assert.Throws<InvalidOperationException>(() => operation.Cancel());
        Assert.False(operation.IsSettled);
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        NativeDnsServiceApi.DispatchResolve(995, new(operation.Token), IntPtr.Zero, calls);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public void InlineResultDuringStartRetainsInputsAfterNativeStartReturns()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var api = new NativeDnsServiceApi(calls);
        IDnsServiceOperation? operation = null;
        var callbacks = 0;
        var callbackHeldInputs = false;
        var freeHeldInputs = false;
        var nativeReturnHeldInputs = false;
        var settled = 0;
        operation = api.CreateResolve(Name, Type, 17, (_, record) =>
        {
            callbacks++;
            callbackHeldInputs = !operation!.IsSettled && NativeDnsServiceApi.AllocatedBufferCount == before.Buffers + 3 && record?.Address == "10.0.0.1";
        });
        operation.Settled += () => settled++;
        calls.BeforeFree = _ => freeHeldInputs = !operation.IsSettled && NativeDnsServiceApi.AllocatedBufferCount == before.Buffers + 3;
        calls.DuringStart = call =>
        {
            // Invoke the actual unmanaged function pointer written into the request.
            var callback = Marshal.GetDelegateForFunctionPointer<NativeDnsServiceApi.ResolveCallback>(call.Callback);
            callback(0, call.Context, calls.Instance());
            nativeReturnHeldInputs = !operation.IsSettled && calls.ReadRequestName(call.Request) == Name && Marshal.ReadIntPtr(call.Cancel) == call.Context;
        };
        Assert.Equal(9506u, operation.Start());
        Assert.True(callbackHeldInputs);
        Assert.True(freeHeldInputs);
        Assert.True(nativeReturnHeldInputs);
        Assert.False(operation.IsSettled);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.Equal(0, settled);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void SuccessfulOrFailedCancelDoesNotProveNativeSettlement(uint status)
    {
        using var calls = new FakeNativeCalls { CancelStatus = status };
        var before = Counters.Read();
        var callbacks = 0;
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => callbacks++);
        operation.Start();
        Assert.Equal(status, operation.Cancel());
        Assert.False(operation.IsSettled);
        Assert.Equal(before.Owners + 1, NativeDnsServiceApi.CurrentOwnerCount);
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        operation.Cancel();
        Assert.Equal(1, calls.CancelCalls);
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(), calls);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public void ResultDuringCancelRetainsInputsAfterNativeCancelReturns()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var callbacks = 0;
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => callbacks++);
        operation.Start();
        var duringFree = false;
        var afterDispatch = false;
        calls.BeforeFree = _ => duringFree = !operation.IsSettled && NativeDnsServiceApi.AllocatedBufferCount == before.Buffers + 3;
        calls.DuringCancel = call =>
        {
            NativeDnsServiceApi.DispatchResolve(995, call.Context, calls.Instance(), calls);
            afterDispatch = !operation.IsSettled && calls.ReadRequestName(call.Request) == Name && Marshal.ReadIntPtr(call.Cancel) == call.Context;
        };
        operation.Cancel();
        Assert.True(duringFree);
        Assert.True(afterDispatch);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public async Task CancelWhileStartIsBlockedQueuesNativeCancellationWithoutWaitingOrFreeing()
    {
        using var calls = new FakeNativeCalls();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => { });
        calls.DuringStart = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Start barrier was not released.");
        };
        calls.DuringCancel = call => NativeDnsServiceApi.DispatchResolve(995, call.Context, IntPtr.Zero, calls);
        var start = Task.Run(operation.Start);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Native start did not reach the barrier.");
        try
        {
            await Task.Run(operation.Cancel).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(operation.IsSettled);
            Assert.Equal(0, calls.CancelCalls);
            Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        }
        finally { release.Set(); }
        Assert.Equal(9506u, await start.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, calls.CancelCalls);
        Assert.False(operation.IsSettled);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public async Task CancelWhileManagedResultCallbackIsBlockedCannotReleaseInputs()
    {
        using var calls = new FakeNativeCalls();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var before = Counters.Read();
        var callbackCompleted = false;
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Callback barrier was not released.");
            callbackCompleted = true;
        });
        operation.Start();
        var callback = Task.Run(() => NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(), calls));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Resolve callback did not reach the barrier.");
        try
        {
            await Task.Run(operation.Cancel).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(operation.IsSettled);
            Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
            Assert.Equal(0, calls.FreeInstanceCalls);
        }
        finally { release.Set(); }
        await callback.WaitAsync(TimeSpan.FromSeconds(30));
        await NativeDnsServiceApi.PendingCancellationWorkers(calls).WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(operation.IsSettled); Assert.True(callbackCompleted);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public void BrowseCallbacksRemainOwnedUntilTerminalCancellationCallback()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var events = new List<DnsAnnouncement>();
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, records) => events.AddRange(records));
        operation.Start();
        var ptr = calls.PtrRecord(Name, 90, cycle: true);
        NativeDnsServiceApi.DispatchBrowse(0, new(operation.Token), ptr, calls);
        Assert.Equal(new DnsAnnouncement(Name, 90), Assert.Single(events));
        Assert.False(operation.IsSettled);
        Assert.Equal(1, calls.FreeRecordCalls);
        var removed = calls.PtrRecord(Name, 0);
        Marshal.WriteInt32(removed, 20, 1); // Positive flags must not replace the TTL-zero removal field.
        NativeDnsServiceApi.DispatchBrowse(0, new(operation.Token), removed, calls);
        Assert.Equal(0u, events[^1].Ttl);
        operation.Cancel();
        Assert.False(operation.IsSettled);
        NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), calls.PtrRecord(Name, 90), calls);
        Assert.True(operation.IsSettled);
        Assert.Equal(2, events.Count);
        Assert.Equal(3, calls.FreeRecordCalls);
        before.AssertRestored(released: 1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public async Task CancellationFromOwnBrowseCallbackNeverWaitsForThatCallbackToReturn(uint callbackStatus)
    {
        using var calls = new FakeNativeCalls();
        using var allowReturn = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        var before = Counters.Read();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDnsServiceOperation? operation = null;
        uint firstCancel = uint.MaxValue, repeatedCancel = uint.MaxValue;
        var nativeHeldInputs = false;
        var settlementCount = 0;
        var failureCount = 0;
        operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) =>
        {
            firstCancel = operation!.Cancel(); repeatedCancel = operation.Cancel();
            continued.TrySetResult();
            if (!allowReturn.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Browse return barrier was not released.");
        });
        operation.Settled += () => { Interlocked.Increment(ref settlementCount); settled.TrySetResult(); };
        operation.CancellationFailed += _ => Interlocked.Increment(ref failureCount);
        operation.Start();
        calls.DuringCancel = call =>
        {
            // Models an allowed cancellation implementation joining its callback.
            // Cancellation on the triggering callback stack would self-deadlock.
            if (!returned.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation waited for the triggering callback to return.");
            nativeHeldInputs = !operation.IsSettled && calls.ReadRequestName(call.Request) == Type && NativeDnsServiceApi.AllocatedBufferCount == before.Buffers + 3;
            NativeDnsServiceApi.DispatchBrowse(1223, call.Context, IntPtr.Zero, calls);
        };
        var records = calls.PtrRecord(Name, 90);
        var callback = Task.Run(() =>
        {
            try { NativeDnsServiceApi.DispatchBrowse(callbackStatus, new(operation.Token), records, calls); }
            finally { returned.Set(); }
        });
        try
        {
            await continued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0u, firstCancel); Assert.Equal(0u, repeatedCancel);
            Assert.Equal(0, calls.CancelCalls);
            Assert.False(operation.IsSettled);
            Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
            allowReturn.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(10));
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(nativeHeldInputs);
            Assert.Equal(1, calls.CancelCalls);
            Assert.Equal(1, calls.FreeRecordCalls);
            Assert.Equal(1, settlementCount);
            Assert.Equal(0, failureCount);
            before.AssertRestored(released: 1);
        }
        finally
        {
            allowReturn.Set(); returned.Set();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallbackCanCancelAnotherOperationWithoutJoiningItsOwnCallbackStack(bool resolveCallback)
    {
        using var calls = new FakeNativeCalls();
        using var allowReturn = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        var before = Counters.Read();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new NativeDnsServiceApi(calls);
        var target = api.CreateBrowse(Type, 17, (_, _) => { });
        uint queuedStatus = uint.MaxValue;
        void Trigger()
        {
            queuedStatus = target.Cancel(); target.Cancel();
            continued.TrySetResult();
            if (!allowReturn.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cross-operation callback barrier was not released.");
        }
        var source = resolveCallback
            ? api.CreateResolve(Name, Type, 17, (_, _) => Trigger())
            : api.CreateBrowse(Type, 17, (_, _) => Trigger());
        target.Settled += () => targetSettled.TrySetResult();
        target.Start(); source.Start();
        var nativeHeldInputs = false;
        calls.DuringCancel = call =>
        {
            if (call.Context != new IntPtr(target.Token)) return;
            cancelEntered.TrySetResult();
            if (!returned.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Canceling B joined the triggering callback of A.");
            nativeHeldInputs = !target.IsSettled && calls.ReadRequestName(call.Request) == Type;
            NativeDnsServiceApi.DispatchBrowse(1223, call.Context, IntPtr.Zero, calls);
        };
        var payload = resolveCallback ? calls.Instance() : calls.PtrRecord(Name, 90);
        var callback = Task.Run(() =>
        {
            try
            {
                if (resolveCallback) NativeDnsServiceApi.DispatchResolve(0, new(source.Token), payload, calls);
                else NativeDnsServiceApi.DispatchBrowse(0, new(source.Token), payload, calls);
            }
            finally { returned.Set(); }
        });
        try
        {
            await continued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0u, queuedStatus);
            Assert.Equal(1, calls.CancelCalls);
            Assert.False(target.IsSettled);
            Assert.Equal(before.Buffers + 6, NativeDnsServiceApi.AllocatedBufferCount);
            allowReturn.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(10));
            await targetSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(nativeHeldInputs);
            Assert.Equal(1, calls.CancelCalls);
            Assert.Equal(1, calls.FreeInstanceCalls + calls.FreeRecordCalls);
        }
        finally
        {
            allowReturn.Set(); returned.Set();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                NativeDnsServiceApi.DispatchBrowse(1223, new(target.Token), IntPtr.Zero, calls);
                if (resolveCallback) NativeDnsServiceApi.DispatchResolve(995, new(source.Token), IntPtr.Zero, calls);
                else { source.Cancel(); NativeDnsServiceApi.DispatchBrowse(1223, new(source.Token), IntPtr.Zero, calls); }
                await targetSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        calls.SettleTestBoundary(); before.AssertRestored(released: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantDiscoveryDisposeCannotJoinTheNativeCallbackThatDrainsPublication(bool concurrentPublisherDrains)
    {
        using var calls = new FakeNativeCalls();
        using var disposeRequested = new ManualResetEventSlim();
        using var allowOtherPublication = new ManualResetEventSlim();
        using var otherBrowseReturned = new ManualResetEventSlim();
        using var allowLastResolveCancellation = new ManualResetEventSlim();
        var before = Counters.Read();
        var api = new TrackingDnsApi(new NativeDnsServiceApi(calls));
        var firstPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRecordCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastResolveCancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherResolveCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new ConcurrentQueue<(NativeCall Call, string? Name)>();
        var errors = new ConcurrentQueue<string>();
        const string otherName = "Kitchen._airplay._tcp.local";
        using var discovery = new WindowsDiscovery(api, new FixedClock(), () => [], message =>
        {
            if (!message.StartsWith("Discovery resolved:", StringComparison.Ordinal) || !message.Contains(otherName, StringComparison.Ordinal)) return;
            otherRecordCommitted.TrySetResult();
            // Hold B before Publish when A must take the deferred batch. A's native
            // cancellation releases this gate; no scheduler winner is assumed.
            if (!concurrentPublisherDrains && !allowOtherPublication.Wait(TimeSpan.FromSeconds(10)))
                errors.Enqueue("The concurrent publication gate was not released.");
        });
        var publication = typeof(WindowsDiscovery).GetField("_publication", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(discovery)!;
        calls.DuringStart = call =>
        {
            var name = calls.ReadRequestName(call.Request);
            starts.Enqueue((call, name));
            if (call.Resolve && name == otherName)
                NativeDnsServiceApi.DispatchResolve(0, call.Context,
                    calls.Instance(count: 1, withTxt: true, address: [10, 0, 0, 2], deviceId: "AA:BB:CC:DD:EE:02"), calls);
        };
        discovery.Start();
        var browse = starts.Single(entry => !entry.Call.Resolve && entry.Name == Type).Call;
        NativeDnsServiceApi.DispatchBrowse(0, browse.Context, calls.PtrRecord(Name, 3600), calls);
        var firstResolve = starts.Single(entry => entry.Call.Resolve && entry.Name == Name).Call;
        calls.BeforeCancel = call =>
        {
            if (!call.Resolve || call.Context != firstResolve.Context) return;
            // Callback/publication completion and browse settlement cannot join
            // this separate resolve-cancellation worker. Hold it before counting
            // its native call so the early-count race has a controlled schedule.
            lastResolveCancellationEntered.TrySetResult();
            if (!allowLastResolveCancellation.Wait(TimeSpan.FromSeconds(10)))
                errors.Enqueue("The final resolve cancellation gate was not released.");
        };
        discovery.Changed += receivers =>
        {
            if (receivers.Count != 1 || receivers[0].Address != "10.0.0.1") return;
            firstPublication.TrySetResult();
            if (!disposeRequested.Wait(TimeSpan.FromSeconds(10))) errors.Enqueue("The dispose gate was not released.");
            discovery.Dispose();
        };
        calls.DuringCancel = call =>
        {
            if (Monitor.IsEntered(publication)) errors.Enqueue("Native cancellation ran on the publication lock stack.");
            allowOtherPublication.Set();
            // This native boundary may join the in-flight browse callback. Calling
            // it on B's own nested resolve/publication stack would self-deadlock.
            if (!otherBrowseReturned.Wait(TimeSpan.FromSeconds(10)))
                errors.Enqueue("Native cancellation joined its triggering browse callback.");
            Assert.NotNull(calls.ReadRequestName(call.Request));
            Assert.Equal(call.Context, Marshal.ReadIntPtr(call.Cancel));
            if (!call.Resolve) NativeDnsServiceApi.DispatchBrowse(1223, call.Context, IntPtr.Zero, calls);
            else if (call.Context != firstResolve.Context) otherResolveCancelled.TrySetResult();
        };
        Task first = Task.CompletedTask, other = Task.CompletedTask;
        try
        {
            first = Task.Factory.StartNew(() =>
            {
                void Resolve() => NativeDnsServiceApi.DispatchResolve(0, firstResolve.Context, calls.Instance(count: 1, withTxt: true), calls);
                // An additional monitor frame makes A's Publish finally defer its
                // drain. B then drains while still inside the real native browse
                // callback, including its inline StartResolve/Resolve frames.
                if (concurrentPublisherDrains) { lock (publication) Resolve(); }
                else Resolve();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
            other = Task.Factory.StartNew(() =>
            {
                try { NativeDnsServiceApi.DispatchBrowse(0, browse.Context, calls.PtrRecord(otherName, 3600), calls); }
                finally { otherBrowseReturned.Set(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await otherRecordCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, discovery.Snapshot.CachedRecords);
            disposeRequested.Set();
            await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(api.BrowseSettlementTasks).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(() => discovery.Snapshot.NativeOwners == 2, TimeSpan.FromSeconds(10)),
                "Native settlement did not reach the discovery owner registry.");
            await lastResolveCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await otherResolveCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, calls.CancelCalls);
            Assert.Equal(Name, calls.ReadRequestName(firstResolve.Request));
            Assert.Equal(firstResolve.Context, Marshal.ReadIntPtr(firstResolve.Cancel));
            Assert.Equal(before.Owners + 2, NativeDnsServiceApi.CurrentOwnerCount);
            Assert.Equal(before.Buffers + 6, NativeDnsServiceApi.AllocatedBufferCount);
            Assert.Equal(before.Released + 2, NativeDnsServiceApi.ReleasedOwnerCount);
            var cancellationWorkers = NativeDnsServiceApi.PendingCancellationWorkers(calls);
            Assert.False(cancellationWorkers.IsCompleted);
            allowLastResolveCancellation.Set();
            await cancellationWorkers.WaitAsync(TimeSpan.FromSeconds(10));
            // Successful cancellation does not settle resolve ownership. Only
            // the joined synthetic boundary below can reclaim these inputs.
            Assert.Equal(before.Owners + 2, NativeDnsServiceApi.CurrentOwnerCount);
            Assert.Equal(before.Buffers + 6, NativeDnsServiceApi.AllocatedBufferCount);
            Assert.Equal(before.Released + 2, NativeDnsServiceApi.ReleasedOwnerCount);
            Assert.Empty(errors);
            Assert.Equal(4, calls.CancelCalls);
            Assert.Equal(2, calls.FreeRecordCalls);
            Assert.Equal(2, calls.FreeInstanceCalls);
            Assert.Equal(0, discovery.Snapshot.CachedRecords);
            Assert.Equal(0, discovery.Snapshot.PendingResolves);
            Assert.Equal(2, discovery.Snapshot.NativeOwners);
            calls.SettleTestBoundary(); before.AssertRestored(released: 4);
        }
        finally
        {
            disposeRequested.Set(); allowOtherPublication.Set(); otherBrowseReturned.Set(); allowLastResolveCancellation.Set();
            try { await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                discovery.Dispose();
                foreach (var operation in api.Operations)
                    if (operation.IsResolve) NativeDnsServiceApi.DispatchResolve(995, new(operation.Token), IntPtr.Zero, calls);
                    else NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
                await Task.WhenAll(api.BrowseSettlementTasks).WaitAsync(TimeSpan.FromSeconds(10));
                await NativeDnsServiceApi.PendingCancellationWorkers(calls).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCancellationStatusAndExceptionFailuresSignalOnceWithoutReleasingInputs(bool throws)
    {
        using var calls = new FakeNativeCalls { CancelStatus = 5, CancelError = throws ? new InvalidOperationException("Fixture queued cancellation failure.") : null };
        using var returned = new ManualResetEventSlim();
        var before = Counters.Read();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCount = 0;
        IDnsServiceOperation? operation = null;
        operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => { operation!.Cancel(); operation.Cancel(); });
        operation.CancellationFailed += _ => throw new InvalidOperationException("Fixture diagnostic subscriber failure.");
        operation.CancellationFailed += message => { Interlocked.Increment(ref failureCount); failure.TrySetResult(message); };
        operation.Settled += () => settled.TrySetResult();
        operation.Start();
        calls.DuringCancel = _ =>
        {
            if (!returned.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Queued cancel joined its callback.");
        };
        var callback = Task.Run(() =>
        {
            try { NativeDnsServiceApi.DispatchBrowse(0, new(operation.Token), IntPtr.Zero, calls); }
            finally { returned.Set(); }
        });
        try
        {
            await callback.WaitAsync(TimeSpan.FromSeconds(10));
            var message = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(message));
            if (throws) Assert.Contains("Fixture queued cancellation failure", message);
            Assert.Equal(1, failureCount);
            Assert.Equal(1, calls.CancelCalls);
            Assert.False(operation.IsSettled);
            Assert.Equal(before.Owners + 1, NativeDnsServiceApi.CurrentOwnerCount);
            Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
            if (!throws) Assert.Equal(5u, operation.Cancel());
            else operation.Cancel();
            Assert.Equal(1, failureCount);
            Assert.Equal(1, calls.CancelCalls);
            NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
            before.AssertRestored(released: 1);
        }
        finally
        {
            returned.Set();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Theory]
    [InlineData(0, 0u, false)]
    [InlineData(7000, 257u, false)]
    [InlineData(7000, 0x80000000u, false)]
    [InlineData(7000, uint.MaxValue, false)]
    [InlineData(7000, 1u, false)]
    [InlineData(7000, 1u, true)]
    public void MalformedInstanceArraysOrPortAreRejectedAndFreedExactlyOnce(int port, uint count, bool keysOnly)
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var callbacks = 0;
        ServiceRecord? result = new("unexpected", Type, "10.0.0.1", 7000, new Dictionary<string, string>());
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, record) => { callbacks++; result = record; });
        operation.Start();
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance((ushort)port, count, keysOnly: keysOnly), calls);
        Assert.Equal(1, callbacks);
        Assert.Null(result);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public void ValidInstanceCopiesAddressPortAndTxtBeforeNativeResultIsFreed()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        ServiceRecord? result = null;
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, record) => result = record);
        operation.Start();
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(7001, 1, withTxt: true), calls);
        Assert.NotNull(result);
        Assert.Equal(Name, result.Instance);
        Assert.Equal(Type, result.ServiceType);
        Assert.Equal("10.0.0.1", result.Address);
        Assert.Equal(7001, result.Port);
        Assert.Equal("AA:BB:CC:DD:EE:FF", result.Txt["deviceid"]);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled); calls.SettleTestBoundary(); before.AssertRestored(released: 1);
    }

    [Fact]
    public void ManagedCallbackAndSettlementSubscriberExceptionsCannotLeakOwnerOrData()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => throw new InvalidOperationException("Fixture callback failure."));
        var secondSubscriber = 0;
        operation.Settled += () => throw new InvalidOperationException("Fixture settlement failure.");
        operation.Settled += () => secondSubscriber++;
        operation.Start();
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(), calls);
        Assert.Equal(0, secondSubscriber);
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled);
        calls.SettleTestBoundary();
        Assert.Equal(1, secondSubscriber);
        before.AssertRestored(released: 1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData(995u)]
    [InlineData(1223u)]
    public void RepeatedAndLateResolveStatusesNeverProveNativeQuiescence(uint lateStatus)
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var records = new List<ServiceRecord?>();
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, record) => records.Add(record));
        operation.Start();
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(), calls);
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(address: [192, 0, 2, 2]), calls);
        Assert.Equal(2, records.Count);
        Assert.Equal("192.0.2.2", records[1]!.Address);
        Assert.False(operation.IsSettled);
        calls.AssertInputsValid(operation.Token, Name);
        operation.Cancel();
        NativeDnsServiceApi.DispatchResolve(lateStatus, new(operation.Token), calls.Instance(), calls);
        Assert.Equal(2, records.Count);
        Assert.Equal(3, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled);
        calls.AssertInputsValid(operation.Token, Name);
        Assert.Equal(before.Owners + 1, NativeDnsServiceApi.CurrentOwnerCount);
        Assert.Equal(before.Buffers + 3, NativeDnsServiceApi.AllocatedBufferCount);
        calls.SettleTestBoundary(); // Synthetic end of API lifetime, independently of status.
        Assert.True(operation.IsSettled);
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void ResultFreeAndFailureObserverExceptionsCannotEscapeTheUnmanagedThunk()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateResolve(Name, Type, 17, (_, _) => { });
        var failures = new List<string>();
        operation.CallbackFailed += _ => throw new InvalidOperationException("Synthetic observer failure.");
        operation.CallbackFailed += failures.Add;
        operation.Start();
        calls.BeforeFree = _ => throw new IOException("Synthetic result cleanup failure.");
        var pointer = calls.Request(operation.Token).Callback;
        var callback = Marshal.GetDelegateForFunctionPointer<NativeDnsServiceApi.ResolveCallback>(pointer);
        Assert.Null(Record.Exception(() => callback(0, new(operation.Token), calls.Instance())));
        Assert.Contains("Synthetic result cleanup failure.", Assert.Single(failures));
        Assert.Equal(1, calls.FreeInstanceCalls);
        Assert.False(operation.IsSettled);
        calls.AssertInputsValid(operation.Token, Name);
        // Unknown context cleanup also cannot throw into the native caller.
        Assert.Null(Record.Exception(() => NativeDnsServiceApi.DispatchResolve(0, new(long.MaxValue), calls.Instance(), calls)));
        Assert.Equal(2, calls.FreeInstanceCalls);
        calls.SettleTestBoundary();
        before.AssertRestored(released: 1);
    }

    [Fact]
    public void StaleUnknownAndWrongKindCallbacksFreeTheirOwnResultsWithoutChangingAnotherOwner()
    {
        using var calls = new FakeNativeCalls();
        var before = Counters.Read();
        var operation = new NativeDnsServiceApi(calls).CreateBrowse(Type, 17, (_, _) => { });
        operation.Start();
        NativeDnsServiceApi.DispatchResolve(0, new(operation.Token), calls.Instance(), calls);
        Assert.False(operation.IsSettled);
        NativeDnsServiceApi.DispatchResolve(0, new(long.MaxValue), calls.Instance(), calls);
        NativeDnsServiceApi.DispatchBrowse(0, new(long.MaxValue), calls.PtrRecord(Name, 90), calls);
        operation.Cancel();
        NativeDnsServiceApi.DispatchBrowse(1223, new(operation.Token), IntPtr.Zero, calls);
        NativeDnsServiceApi.DispatchBrowse(0, new(operation.Token), calls.PtrRecord(Name, 90), calls);
        Assert.Equal(2, calls.FreeInstanceCalls);
        Assert.Equal(2, calls.FreeRecordCalls);
        before.AssertRestored(released: 1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void GlobalOwnerLimitIncludesCanceledOperationsAcrossApiInstancesAndRecoversAfterSettlement(uint cancelStatus)
    {
        using var calls = new FakeNativeCalls { CancelStatus = cancelStatus };
        var before = Counters.Read();
        var first = new NativeDnsServiceApi(calls);
        var second = new NativeDnsServiceApi(calls);
        var operations = new List<IDnsServiceOperation>();
        try
        {
            for (var index = 0; index < NativeDnsServiceApi.OwnerLimit; index++)
            {
                var operation = (index % 2 == 0 ? first : second).CreateResolve($"{index}.{Name}", Type, 17, (_, _) => { });
                operations.Add(operation);
                operation.Start(); operation.Cancel();
            }
            Assert.Equal(NativeDnsServiceApi.OwnerLimit, NativeDnsServiceApi.CurrentOwnerCount);
            Assert.Equal(before.Buffers + NativeDnsServiceApi.OwnerLimit * 3, NativeDnsServiceApi.AllocatedBufferCount);
            Assert.Throws<DnsServiceCapacityException>(() => second.CreateBrowse(Type, 17, (_, _) => { }));
            Assert.Equal(before.Buffers + NativeDnsServiceApi.OwnerLimit * 3, NativeDnsServiceApi.AllocatedBufferCount);
            NativeDnsServiceApi.DispatchResolve(995, new(operations[0].Token), IntPtr.Zero, calls);
            Assert.Throws<DnsServiceCapacityException>(() => first.CreateBrowse(Type, 17, (_, _) => { }));
            Assert.Equal(NativeDnsServiceApi.OwnerLimit, NativeDnsServiceApi.CurrentOwnerCount);
            calls.SettleTestBoundary(); // All fake native workers are known finished; Windows has no such signal.
            var replacement = first.CreateBrowse(Type, 17, (_, _) => { });
            operations.Add(replacement);
            Assert.Equal(1, NativeDnsServiceApi.CurrentOwnerCount);
            replacement.Cancel();
        }
        finally
        {
            foreach (var operation in operations)
            {
                if (operation.IsResolve) NativeDnsServiceApi.DispatchResolve(995, new(operation.Token), IntPtr.Zero, calls);
                else operation.Cancel();
            }
        }
        before.AssertRestored(released: NativeDnsServiceApi.OwnerLimit + 1);
    }

    private readonly record struct Counters(int Owners, long Buffers, long Released)
    {
        public static Counters Read() => new(NativeDnsServiceApi.CurrentOwnerCount, NativeDnsServiceApi.AllocatedBufferCount, NativeDnsServiceApi.ReleasedOwnerCount);
        public void AssertRestored(long released)
        {
            Assert.Equal(Owners, NativeDnsServiceApi.CurrentOwnerCount);
            Assert.Equal(Buffers, NativeDnsServiceApi.AllocatedBufferCount);
            Assert.Equal(Released + released, NativeDnsServiceApi.ReleasedOwnerCount);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class TrackingDnsApi(IDnsServiceApi inner) : IDnsServiceApi
    {
        public readonly ConcurrentQueue<IDnsServiceOperation> Operations = new();
        public readonly ConcurrentQueue<Task> SettlementTasks = new();
        public readonly ConcurrentQueue<Task> BrowseSettlementTasks = new();
        public IDnsServiceOperation CreateBrowse(string type, uint index, Action<uint, IReadOnlyList<DnsAnnouncement>> callback)
            => Track(inner.CreateBrowse(type, index, callback));
        public IDnsServiceOperation CreateResolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback)
            => Track(inner.CreateResolve(name, type, index, callback));
        private IDnsServiceOperation Track(IDnsServiceOperation operation)
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Settled += () => settled.TrySetResult();
            Operations.Enqueue(operation); SettlementTasks.Enqueue(settled.Task);
            if (!operation.IsResolve) BrowseSettlementTasks.Enqueue(settled.Task);
            return operation;
        }
    }

    private sealed record NativeCall(IntPtr Request, IntPtr Cancel, IntPtr Context, IntPtr Callback, bool Resolve);
    private sealed class FakeNativeCalls : INativeDnsCalls, IDisposable
    {
        private readonly ConcurrentDictionary<IntPtr, List<IntPtr>> _results = new();
        private readonly ConcurrentDictionary<IntPtr, NativeCall> _calls = new();
        public uint StartStatus = 9506, CancelStatus;
        public Exception? StartError, CancelError;
        public int StartCalls, CancelCalls, FreeInstanceCalls, FreeRecordCalls;
        public Action<NativeCall>? DuringStart, BeforeCancel, DuringCancel;
        public Action<IntPtr>? BeforeFree;
        public uint StartResolve(IntPtr request, IntPtr cancel) => Start(request, cancel, true);
        public uint StartBrowse(IntPtr request, IntPtr cancel) => Start(request, cancel, false);
        private uint Start(IntPtr request, IntPtr cancel, bool resolve)
        {
            Interlocked.Increment(ref StartCalls);
            var input = Marshal.PtrToStructure<NativeDnsServiceApi.NativeServiceRequest>(request);
            var call = new NativeCall(request, cancel, input.Context, input.Callback, resolve);
            _calls[cancel] = call;
            Marshal.WriteIntPtr(cancel, input.Context);
            DuringStart?.Invoke(call);
            if (StartError is not null) throw StartError;
            return StartStatus;
        }
        public uint CancelResolve(IntPtr cancel) => Cancel(cancel);
        public uint CancelBrowse(IntPtr cancel) => Cancel(cancel);
        private uint Cancel(IntPtr cancel)
        {
            var call = _calls[cancel];
            BeforeCancel?.Invoke(call);
            Interlocked.Increment(ref CancelCalls); DuringCancel?.Invoke(call);
            if (CancelError is not null) throw CancelError;
            return CancelStatus;
        }
        public string? ReadRequestName(IntPtr request) => Marshal.PtrToStringUni(Marshal.PtrToStructure<NativeDnsServiceApi.NativeServiceRequest>(request).QueryName);
        public NativeCall Request(long token) => _calls.Values.Single(call => call.Context == new IntPtr(token));
        public void AssertInputsValid(long token, string name)
        {
            var call = Request(token);
            Assert.Equal(name, ReadRequestName(call.Request));
            Assert.Equal(call.Context, Marshal.ReadIntPtr(call.Cancel));
        }
        public IntPtr Instance(ushort port = 7000, uint count = 0, bool keysOnly = false, bool withTxt = false,
            byte[]? address = null, string deviceId = "AA:BB:CC:DD:EE:FF")
        {
            var buffers = new List<IntPtr>();
            var addressBuffer = Allocate(4, buffers); Marshal.Copy(address ?? [10, 0, 0, 1], 0, addressBuffer, 4);
            var info = new NativeDnsServiceApi.NativeServiceInstance { Ipv4 = addressBuffer, Port = port, Count = count };
            if (keysOnly || withTxt)
            {
                info.Keys = Allocate(IntPtr.Size, buffers);
                var key = Marshal.StringToHGlobalUni("deviceid"); buffers.Add(key); Marshal.WriteIntPtr(info.Keys, key);
            }
            if (withTxt)
            {
                info.Values = Allocate(IntPtr.Size, buffers);
                var value = Marshal.StringToHGlobalUni(deviceId); buffers.Add(value); Marshal.WriteIntPtr(info.Values, value);
            }
            var result = Allocate(Marshal.SizeOf<NativeDnsServiceApi.NativeServiceInstance>(), buffers);
            Marshal.StructureToPtr(info, result, false);
            _results[result] = buffers;
            return result;
        }
        public IntPtr PtrRecord(string name, uint ttl, bool cycle = false)
        {
            var buffers = new List<IntPtr>();
            var result = Allocate(40, buffers);
            for (var index = 0; index < 40; index++) Marshal.WriteByte(result, index, 0);
            var text = Marshal.StringToHGlobalUni(name); buffers.Add(text);
            Marshal.WriteIntPtr(result, cycle ? result : IntPtr.Zero);
            Marshal.WriteInt16(result, 16, 12);
            Marshal.WriteInt32(result, 24, unchecked((int)ttl));
            Marshal.WriteIntPtr(result, 32, text);
            _results[result] = buffers;
            return result;
        }
        private static IntPtr Allocate(int bytes, List<IntPtr> buffers) { var result = Marshal.AllocHGlobal(bytes); buffers.Add(result); return result; }
        public void FreeInstance(IntPtr instance) { Interlocked.Increment(ref FreeInstanceCalls); Free(instance); }
        public void FreeRecords(IntPtr records) { Interlocked.Increment(ref FreeRecordCalls); Free(records); }
        private void Free(IntPtr root)
        {
            BeforeFree?.Invoke(root);
            if (!_results.TryRemove(root, out var buffers)) throw new InvalidOperationException("Native callback data was freed more than once or by the wrong allocator.");
            foreach (var buffer in buffers) Marshal.FreeHGlobal(buffer);
        }
        public void Dispose()
        {
            foreach (var call in _calls.Values)
            {
                if (call.Resolve) NativeDnsServiceApi.DispatchResolve(995, call.Context, IntPtr.Zero, this);
                else NativeDnsServiceApi.DispatchBrowse(1223, call.Context, IntPtr.Zero, this);
            }
            foreach (var buffers in _results.Values) foreach (var buffer in buffers) Marshal.FreeHGlobal(buffer);
            _results.Clear();
            SettleTestBoundary();
        }
        public void SettleTestBoundary()
        {
            // Callback tasks are joined by each gated fixture before teardown.
            // Join the production cancellation workers too, then explicitly end
            // this synthetic boundary. No Windows cancellation result is inferred.
            NativeDnsServiceApi.PendingCancellationWorkers(this).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            NativeDnsServiceApi.SettleQuiescentTestBoundary(this);
        }
    }
}
