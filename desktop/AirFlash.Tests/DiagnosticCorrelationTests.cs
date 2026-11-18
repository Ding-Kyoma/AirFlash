using System.Text.Json;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class DiagnosticCorrelationTests
{
    private static readonly TimeSpan Limit = SessionTestEngine.Limit;

    [Fact]
    public async Task A_late_cancelled_reply_cannot_complete_a_later_request()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        using var cancellation = new CancellationTokenSource();
        var first = controller.DiagnosticSnapshotAsync(cancellation.Token);
        await connection.WaitForAsync("diag_summary");
        var firstId = connection.LastRequestId("diag_summary");
        cancellation.Cancel();
        Assert.Null(await first.WaitAsync(Limit));

        var second = controller.DiagnosticSnapshotAsync();
        await connection.WaitForAsync("diag_summary", 2);
        var secondId = connection.LastRequestId("diag_summary");
        Reply(connection, firstId, 7, "old");
        await DrainAsync(controller, connection);
        Assert.False(second.IsCompleted, "a cancelled request's reply must not answer a later export");

        Reply(connection, secondId, 7, "current");
        Assert.Equal("current", (await second.WaitAsync(Limit))!.Value.GetProperty("session").Text("marker"));
    }

    [Fact]
    public async Task A_reply_with_the_previous_native_epoch_is_not_a_current_summary()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 8);
        var snapshot = controller.DiagnosticSnapshotAsync();
        await connection.WaitForAsync("diag_summary");
        Reply(connection, connection.LastRequestId("diag_summary"), 7, "previous transport");

        Assert.Null(await snapshot.WaitAsync(Limit));
    }


    [Fact]
    public async Task A_reply_received_before_send_returns_is_already_registered()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            OnCommand = (sender, command) =>
            {
                if (command == "diag_summary") Reply(sender, sender.LastRequestId(command), 7, "inline");
            },
            AfterCommandAsync = command =>
            {
                if (command != "diag_summary") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        var capture = controller.CaptureDiagnosticsAsync();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await DrainAsync(controller, connection);
            Assert.False(capture.IsCompleted);
            release.TrySetResult();
            Assert.Equal("inline", Marker((await capture.WaitAsync(Limit)).Summary));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unmatched_reply_cannot_complete_or_cache_a_snapshot(bool omitId)
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        using var cancellation = new CancellationTokenSource();
        var capture = controller.CaptureDiagnosticsAsync(cancellation.Token);
        await connection.WaitForAsync("diag_summary");
        connection.Current("diag_summary", omitId
            ? new { session = new { session_epoch = 7, marker = "unsolicited" } }
            : (object)new { id = "wrong-request", session = new { session_epoch = 7, marker = "unsolicited" } });
        await DrainAsync(controller, connection);
        Assert.False(capture.IsCompleted);
        cancellation.Cancel();
        Assert.Null((await capture.WaitAsync(Limit)).Summary);
        await controller.StopAsync();
        Assert.Null((await controller.CaptureDiagnosticsAsync()).Summary);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("text")]
    [InlineData("zero")]
    [InlineData("future")]
    [InlineData("not-object")]
    public async Task Known_epoch_requires_a_valid_matching_nested_epoch(string malformed)
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        var capture = controller.CaptureDiagnosticsAsync();
        await connection.WaitForAsync("diag_summary");
        object? summary = malformed switch
        {
            "missing" => new { packets = 1 },
            "null" => new { session_epoch = (long?)null },
            "text" => new { session_epoch = "seven" },
            "zero" => new { session_epoch = 0 },
            "future" => new { session_epoch = 8 },
            _ => "not a snapshot"
        };
        connection.Current("diag_summary", new { id = connection.LastRequestId("diag_summary"), session = summary });
        Assert.Null((await capture.WaitAsync(Limit)).Summary);
    }

    [Fact]
    public async Task A_legacy_stream_and_summary_without_native_epochs_remain_supported()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, null);
        var capture = DiagnosticExport.CaptureAsync(controller);
        await connection.WaitForAsync("diag_summary");
        connection.Current("diag_summary", new
        {
            id = connection.LastRequestId("diag_summary"), session = new { marker = "legacy", packets = 12 }
        });
        Assert.Equal("legacy", Marker((await capture.WaitAsync(Limit)).Summary));
    }

    [Fact]
    public async Task Transport_metrics_can_supply_an_epoch_missing_from_the_streaming_event()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, null);
        var capture = controller.CaptureDiagnosticsAsync();
        await connection.WaitForAsync("diag_summary");
        connection.Current("transport_metrics", new { session_epoch = 7, session_uptime_ms = 1000, members = Array.Empty<object>() });
        Reply(connection, connection.LastRequestId("diag_summary"), 7, "learned");
        Assert.Equal("learned", Marker((await capture.WaitAsync(Limit)).Summary));
    }

    [Fact]
    public async Task A_known_epoch_learned_before_return_also_validates_an_early_reply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            OnCommand = (sender, command) =>
            {
                if (command == "diag_summary") Reply(sender, sender.LastRequestId(command), null, "too early");
            },
            AfterCommandAsync = command =>
            {
                if (command != "diag_summary") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, null);
        var capture = controller.CaptureDiagnosticsAsync();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await DrainAsync(controller, connection);
            connection.Current("transport_metrics", new { session_epoch = 7, session_uptime_ms = 1000, members = Array.Empty<object>() });
            await DrainAsync(controller, connection);
            release.TrySetResult();
            Assert.Null((await capture.WaitAsync(Limit)).Summary);
            await controller.StopAsync();
            Assert.Null((await controller.CaptureDiagnosticsAsync()).Summary);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Standby_replacement_ends_a_pending_capture_and_clears_the_old_cache()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        await CaptureCountersAsync(controller, connection, 5);
        var accepted = await CaptureWithReplyAsync(controller, connection, 7, "first");
        var pending = controller.CaptureDiagnosticsAsync();
        await connection.WaitForAsync("diag_summary", 2);
        var oldId = connection.LastRequestId("diag_summary");

        await StateEventAsync(controller, connection, "standby", PlaybackState.Standby);
        Assert.Null((await pending.WaitAsync(Limit)).Summary);
        await StateEventAsync(controller, connection, "resuming", PlaybackState.Connecting);
        await StateEventAsync(controller, connection, "streaming", PlaybackState.Streaming, new { session_epoch = 8 });
        Reply(connection, oldId, 7, "old transport");
        await DrainAsync(controller, connection);
        var current = await CaptureWithReplyAsync(controller, connection, 8, "second");
        Assert.Equal("second", Marker(current.Summary));
        Assert.Equal("first", Marker(accepted.Summary));
    }

    [Fact]
    public async Task A_new_engine_reusing_the_native_epoch_cannot_reuse_the_old_summary()
    {
        var first = new SessionTestEngine.Connection();
        var second = new SessionTestEngine.Connection();
        await using var controller = Create(first, second);
        await StreamAsync(controller, first, 1);
        await CaptureWithReplyAsync(controller, first, 1, "first process");
        first.Current("error", new { code = "timeline_expired", channel = "scheduler", retryable = true, message = "synthetic retry" });
        await second.Starting.Task.WaitAsync(Limit);
        Assert.Null((await controller.CaptureDiagnosticsAsync()).Summary);
        await StateEventAsync(controller, second, "streaming", PlaybackState.Streaming, new { session_epoch = 1 });
        var capture = await CaptureWithReplyAsync(controller, second, 1, "second process");
        Assert.Equal("second process", Marker(capture.Summary));
    }

    [Fact]
    public async Task Failed_capture_retains_request_time_history_instead_of_a_previous_summary()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        await CaptureCountersAsync(controller, connection, 5);
        await CaptureWithReplyAsync(controller, connection, 7, "previous");
        using var cancellation = new CancellationTokenSource();
        var failed = controller.CaptureDiagnosticsAsync(cancellation.Token);
        await connection.WaitForAsync("diag_summary", 2);
        connection.Current("capture_metrics", new { metrics = new { underrun_packets = 12, padded_frames = 12UL } });
        await DrainAsync(controller, connection);
        cancellation.Cancel();

        var capture = await failed.WaitAsync(Limit);
        Assert.Null(capture.Summary);
        Assert.Equal(5UL, Assert.Single(capture.CounterHistory!).Counters!["padded_frames"]);
        Assert.Equal(2, controller.Snapshot.Diagnostics!.CounterDeltaHistory!.Count);
    }

    [Fact]
    public async Task Accepted_capture_freezes_history_at_receipt_and_retains_it_after_stop()
    {
        var connection = new SessionTestEngine.Connection();
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        await CaptureCountersAsync(controller, connection, 5);
        var capture = await CaptureWithReplyAsync(controller, connection, 7, "accepted");
        var entry = Assert.Single(capture.CounterHistory!);
        Assert.Equal(5UL, entry.Counters!["padded_frames"]);
        var original = Assert.IsAssignableFrom<IDictionary<string, ulong>>(controller.Snapshot.Diagnostics!.CounterDeltaHistory![0].Counters);
        original["padded_frames"] = 99;
        Assert.Equal(5UL, entry.Counters["padded_frames"]);
        var exported = Assert.IsAssignableFrom<IDictionary<string, ulong>>(entry.Counters);
        Assert.Throws<NotSupportedException>(() => exported["padded_frames"] = 88);
        var reports = Assert.IsAssignableFrom<IList<PlaybackCounterDelta>>(capture.CounterHistory);
        Assert.Throws<NotSupportedException>(() => reports.Clear());
        await controller.StopAsync();
        var historical = await controller.CaptureDiagnosticsAsync();
        Assert.Equal("accepted", Marker(historical.Summary));
        Assert.Equal(5UL, Assert.Single(historical.CounterHistory!).Counters!["padded_frames"]);
    }

    [Fact]
    public async Task A_transport_without_correlation_returns_available_history_without_a_current_summary()
    {
        var connection = new SessionTestEngine.Connection();
        var factory = new UncorrelatedFactory(connection);
        await using var controller = TestSessionController.Create(factory, new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        await StreamAsync(controller, connection, 7);
        await CaptureCountersAsync(controller, connection, 5);

        var capture = await controller.CaptureDiagnosticsAsync();

        Assert.Null(capture.Summary);
        Assert.Single(capture.CounterHistory!);
        Assert.Equal(0, connection.Count("diag_summary"));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_receipt_freezes_history_but_a_later_send_failure_does_not_cache_it(bool failSend)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            OnCommand = (sender, command) =>
            {
                if (command == "diag_summary") Reply(sender, sender.LastRequestId(command), 7, "at receipt");
            },
            AfterCommandAsync = async command =>
            {
                if (command != "diag_summary") return;
                entered.TrySetResult();
                await release.Task;
                if (failSend) throw new IOException("synthetic write failure after reply");
            }
        };
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        await CaptureCountersAsync(controller, connection, 5);
        var capture = controller.CaptureDiagnosticsAsync();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await DrainAsync(controller, connection);
            connection.Current("capture_metrics", new { metrics = new { underrun_packets = 12, padded_frames = 12UL } });
            await DrainAsync(controller, connection);
            release.TrySetResult();

            var result = await capture.WaitAsync(Limit);
            Assert.Equal(failSend ? null : "at receipt", Marker(result.Summary));
            Assert.Equal(5UL, Assert.Single(result.CounterHistory!).Counters!["padded_frames"]);
            Assert.Equal(2, controller.Snapshot.Diagnostics!.CounterDeltaHistory!.Count);
            await controller.StopAsync();
            Assert.Equal(failSend ? null : "at receipt", Marker((await controller.CaptureDiagnosticsAsync()).Summary));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_transport_replacement_after_receipt_prevents_a_current_summary()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            OnCommand = (sender, command) =>
            {
                if (command == "diag_summary") Reply(sender, sender.LastRequestId(command), 7, "old transport");
            },
            AfterCommandAsync = command =>
            {
                if (command != "diag_summary") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var controller = Create(connection);
        await StreamAsync(controller, connection, 7);
        var capture = controller.CaptureDiagnosticsAsync();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await DrainAsync(controller, connection);
            await StateEventAsync(controller, connection, "standby", PlaybackState.Standby);
            await StateEventAsync(controller, connection, "resuming", PlaybackState.Connecting);
            await StateEventAsync(controller, connection, "streaming", PlaybackState.Streaming, new { session_epoch = 8 });
            release.TrySetResult();
            Assert.Null((await capture.WaitAsync(Limit)).Summary);
            await controller.StopAsync();
            Assert.Null((await controller.CaptureDiagnosticsAsync()).Summary);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class UncorrelatedFactory(SessionTestEngine.Connection connection) : IEngineFactory
    {
        public IEngineConnection Open() => new UncorrelatedConnection(connection);
    }

    private sealed class UncorrelatedConnection(SessionTestEngine.Connection connection) : IEngineConnection
    {
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
            => connection.SendAsync(session, command, parameters, cancellation);
        public ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) => connection.ReadAsync(cancellation);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private static string? Marker(JsonElement? summary) => summary?.GetProperty("session").Text("marker");

    private static async Task<DiagnosticCapture> CaptureWithReplyAsync(SessionController controller, SessionTestEngine.Connection connection, long? epoch, string marker)
    {
        var count = connection.Count("diag_summary") + 1;
        var capture = controller.CaptureDiagnosticsAsync();
        await connection.WaitForAsync("diag_summary", count);
        Reply(connection, connection.LastRequestId("diag_summary"), epoch, marker);
        return await capture.WaitAsync(Limit);
    }

    private static async Task CaptureCountersAsync(SessionController controller, SessionTestEngine.Connection connection, ulong count)
    {
        connection.Current("capture_metrics", new { metrics = new { underrun_packets = 0, padded_frames = 0UL } });
        connection.Current("capture_metrics", new { metrics = new { underrun_packets = count, padded_frames = count } });
        await DrainAsync(controller, connection);
    }

    private static async Task StateEventAsync(SessionController controller, SessionTestEngine.Connection connection, string kind, PlaybackState state, object? fields = null)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot snapshot)
        {
            if (snapshot.State == state) observed.TrySetResult();
        }
        controller.Changed += Changed;
        try
        {
            connection.Current(kind, fields);
            await observed.Task.WaitAsync(Limit);
        }
        finally { controller.Changed -= Changed; }
    }

    private static SessionController Create(params SessionTestEngine.Connection[] connections)
        => TestSessionController.Create(new SessionTestEngine.Factory(connections), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);

    private static async Task StreamAsync(SessionController controller, SessionTestEngine.Connection connection, long? epoch)
    {
        await controller.StartAsync(SessionTestEngine.Receiver(), SessionTestEngine.Settings());
        await connection.Starting.Task.WaitAsync(Limit);
        connection.Current("streaming", new { session_epoch = epoch });
        await DrainAsync(controller, connection);
        Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
    }

    private static void Reply(SessionTestEngine.Connection connection, string requestId, long? epoch, string marker)
        => connection.Current("diag_summary", new { id = requestId, session = new { session_epoch = epoch, marker } });

    private static async Task DrainAsync(SessionController controller, SessionTestEngine.Connection connection)
    {
        var marker = Guid.NewGuid().ToString("N");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot snapshot)
        {
            if (snapshot.Diagnostics?.Warnings?.Any(warning => warning.Code == marker) == true) observed.TrySetResult();
        }
        controller.Changed += Changed;
        try
        {
            connection.Current("warning", new { code = marker, channel = "test", message = "diagnostics ordering marker" });
            await observed.Task.WaitAsync(Limit);
        }
        finally { controller.Changed -= Changed; }
    }
}
