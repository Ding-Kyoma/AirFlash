using AirFlash.App.Services;
using Xunit;
namespace AirFlash.Tests;

public sealed class ShellShutdownTests
{
    [Fact]
    public async Task AdmissionClosesSynchronouslyBeforeAcceptedSaveAndCleanupSettle()
    {
        List<string> calls = [];
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = ShellShutdown.RunAsync(() => calls.Add("close admission"),
            () => { calls.Add("save"); return save.Task; }, ShellShutdown.Sync(() => calls.Add("windows")),
            async () => { calls.Add("engine"); entered.SetResult(); await cleanup.Task; },
            () => { calls.Add("view model"); return ValueTask.CompletedTask; });
        try
        {
            Assert.Equal(new[] { "close admission", "save" }, calls);
            Assert.False(operation.IsCompleted);
            save.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "close admission", "save", "windows", "engine" }, calls);
            Assert.False(operation.IsCompleted);
        }
        finally { save.TrySetResult(); cleanup.TrySetResult(); await operation; }
        Assert.Equal("view model", calls[^1]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AdmissionNotificationFailureStillAttemptsEveryCleanup(bool engineAlsoFails)
    {
        List<string> calls = [];
        var admissionError = new IOException("synthetic admission notification failure");
        var engineError = new IOException("synthetic engine cleanup failure");
        var operation = ShellShutdown.RunAsync(() => { calls.Add("admission"); throw admissionError; },
            () => { calls.Add("save"); return Task.CompletedTask; }, ShellShutdown.Sync(() => calls.Add("windows")),
            () => { calls.Add("engine"); if (engineAlsoFails) throw engineError; return ValueTask.CompletedTask; },
            () => { calls.Add("view model"); return ValueTask.CompletedTask; });
        var error = await Record.ExceptionAsync(() => operation);
        Assert.Equal(new[] { "admission", "save", "windows", "engine", "view model" }, calls);
        if (engineAlsoFails)
            Assert.Equal(new[] { admissionError, engineError }, Assert.IsType<AggregateException>(error).InnerExceptions);
        else Assert.Same(admissionError, error);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("settings")]
    [InlineData("recovery")]
    [InlineData("engine")]
    [InlineData("view model")]
    [InlineData("tray")]
    [InlineData("panel")]
    [InlineData("instance")]
    [InlineData("theme")]
    [InlineData(null)]
    public async Task EachCleanupRunsAfterEarlierFailuresAndOriginalErrorsArePreserved(string? failure)
    {
        string[] stages = ["save", "settings", "recovery", "engine", "view model", "tray", "panel", "instance", "theme"];
        List<string> calls = [];
        var errors = stages.ToDictionary(stage => stage, stage => new IOException($"synthetic {stage} failure"));
        bool Fails(string stage) => failure is null || failure == stage;
        Func<ValueTask> SyncStep(string stage) => ShellShutdown.Sync(() =>
        {
            calls.Add(stage);
            if (Fails(stage)) throw errors[stage];
        });
        ValueTask AsyncStep(string stage)
        {
            calls.Add(stage);
            return Fails(stage) ? ValueTask.FromException(errors[stage]) : ValueTask.CompletedTask;
        }
        var operation = ShellShutdown.RunAsync(() => calls.Add("admission"),
            () => AsyncStep("save").AsTask(), SyncStep("settings"), SyncStep("recovery"),
            () => AsyncStep("engine"), () => AsyncStep("view model"), SyncStep("tray"),
            SyncStep("panel"), () => AsyncStep("instance"), SyncStep("theme"));
        var error = await Record.ExceptionAsync(() => operation);
        Assert.Equal(new[] { "admission" }.Concat(stages), calls);
        if (failure is null)
            Assert.Equal(stages.Select(stage => errors[stage]), Assert.IsType<AggregateException>(error).InnerExceptions);
        else Assert.Same(errors[failure], error);
    }
}
