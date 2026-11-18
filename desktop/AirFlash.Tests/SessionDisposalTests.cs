using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SessionDisposalTests
{
    private static readonly TimeSpan Limit = SessionTestEngine.Limit;

    [Fact]
    public async Task Disposal_waits_for_an_in_flight_diagnostic_snapshot_and_later_callers_return()
    {
        var connection = new SessionTestEngine.Connection();
        var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        await SessionTestEngine.StreamAsync(controller, connection);
        // The snapshot holds the diagnostic writer while it waits for the reply.
        var snapshot = controller.DiagnosticSnapshotAsync();
        await connection.WaitForAsync("diag_summary");
        // Ending the session answers the pending snapshot before disposal finishes.
        await controller.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.Null(await snapshot.WaitAsync(Limit));
        // Callers that arrive after disposal (a late export, the settings window's
        // fire-and-forget preview clear) return instead of throwing.
        Assert.Null(await controller.DiagnosticSnapshotAsync().WaitAsync(Limit));
        await controller.ClearEqualizerPreviewAsync(Guid.NewGuid()).WaitAsync(Limit);
        await controller.PreviewEqualizerAsync(Guid.NewGuid(), new EqualizerSettings()).WaitAsync(Limit);
    }

    [Fact]
    public async Task A_diagnostic_requested_after_disposal_begins_does_not_wait_for_the_current_export()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            BeforeCommandAsync = command =>
            {
                if (command != "diag_summary") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        Task? first = null;
        Task? queued = null;
        Task? disposal = null;
        try
        {
            await SessionTestEngine.StreamAsync(controller, connection);
            var snapshot = controller.DiagnosticSnapshotAsync();
            first = snapshot;
            await entered.Task.WaitAsync(Limit);
            var waiting = controller.DiagnosticSnapshotAsync();
            queued = waiting;
            Assert.False(waiting.IsCompleted);
            disposal = controller.DisposeAsync().AsTask();
            Assert.Same(disposal, controller.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);

            var late = controller.DiagnosticSnapshotAsync();

            Assert.True(late.IsCompletedSuccessfully, "disposal must reject a late export without waiting on the held diagnostic writer");
            Assert.Null(await late);
            Assert.Equal(0, connection.Count("diag_summary"));

            release.TrySetResult();
            Assert.Null(await snapshot.WaitAsync(Limit));
            Assert.Null(await waiting.WaitAsync(Limit));
            Assert.Equal(1, connection.Count("diag_summary"));
        }
        finally
        {
            release.TrySetResult();
            await (disposal ?? controller.DisposeAsync().AsTask()).WaitAsync(Limit);
            if (first is not null) await first.WaitAsync(Limit);
            if (queued is not null) await queued.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Disposal_drains_equalizer_writes_already_waiting_and_rejects_late_preview()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = 0;
        var connection = new SessionTestEngine.Connection
        {
            BeforeCommandAsync = command =>
            {
                if (command != "set_equalizer" || Volatile.Read(ref block) == 0) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        Task? disposal = null;
        Task? holder = null;
        Task? waiting = null;
        Task? preview = null;
        try
        {
            await SessionTestEngine.StreamAsync(controller, connection);
            var owner = Guid.NewGuid();
            await controller.PreviewEqualizerAsync(owner, new()).WaitAsync(Limit);
            var writes = connection.Count("set_equalizer");
            Volatile.Write(ref block, 1);
            holder = controller.ClearEqualizerPreviewAsync(owner);
            await entered.Task.WaitAsync(Limit);

            var second = Guid.NewGuid();
            preview = controller.PreviewEqualizerAsync(second, new());
            waiting = controller.ClearEqualizerPreviewAsync(second);
            Assert.False(waiting.IsCompleted);
            disposal = controller.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.True(controller.PreviewEqualizerAsync(Guid.NewGuid(), new()).IsCompletedSuccessfully);

            release.TrySetResult();
            await disposal.WaitAsync(Limit);
            Assert.Equal(writes + 1, connection.Count("set_equalizer"));
        }
        finally
        {
            release.TrySetResult();
            await (disposal ?? controller.DisposeAsync().AsTask()).WaitAsync(Limit);
            if (holder is not null) await holder.WaitAsync(Limit);
            if (waiting is not null) await waiting.WaitAsync(Limit);
            if (preview is not null) await preview.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Disposal_cancels_and_joins_a_device_volume_edit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            BeforeCommandAsync = command =>
            {
                if (command != "set_device_volume") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        controller.Changed += snapshot => { if (snapshot.DeviceVolume?.Available == true) volumeReady.TrySetResult(); };
        Task? disposal = null;
        Task? edit = null;
        try
        {
            await SessionTestEngine.StreamAsync(controller, connection);
            var receiver = SessionTestEngine.Receiver();
            connection.Current("device_volume", new { host = receiver.Address, available = true, status = "confirmed", volume = 30, sequence = 0 });
            await volumeReady.Task.WaitAsync(Limit);
            edit = controller.SetDeviceVolumeAsync(receiver.Id, 55);
            await entered.Task.WaitAsync(Limit);

            disposal = controller.DisposeAsync().AsTask();
            await disposal.WaitAsync(Limit);
            await edit.WaitAsync(Limit);
            await controller.SetDeviceVolumeAsync(receiver.Id, 65).WaitAsync(Limit);
            Assert.Equal(0, connection.Count("set_device_volume"));
        }
        finally
        {
            release.TrySetResult();
            await (disposal ?? controller.DisposeAsync().AsTask()).WaitAsync(Limit);
            if (edit is not null) await edit.WaitAsync(Limit);
        }
    }
}
