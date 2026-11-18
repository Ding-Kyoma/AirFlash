using System.Runtime.ExceptionServices;
namespace AirFlash.App.Services;

internal static class ShellShutdown
{
    internal static async Task RunAsync(Action beginShutdown, Func<Task> waitForApply,
        params Func<ValueTask>[] cleanup)
    {
        // This runs synchronously, before any accepted save or probe cleanup can yield.
        List<Exception> errors = [];
        try { beginShutdown(); } catch (Exception error) { errors.Add(error); }
        try { await waitForApply(); } catch (Exception error) { errors.Add(error); }
        // Window operations and release of the single-instance mutex need the caller's dispatcher.
        foreach (var step in cleanup)
        {
            try { await step(); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }

    internal static Func<ValueTask> Sync(Action step) => () =>
    {
        step();
        return ValueTask.CompletedTask;
    };
}
