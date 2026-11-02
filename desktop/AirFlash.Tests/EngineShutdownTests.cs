using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class EngineShutdownTests
{
    private static ProcessEngineConnection Open(string finish, ConcurrentQueue<string> log)
    {
        var script = "$ErrorActionPreference = 'Stop'\n$null = [Console]::ReadLine()\n[Console]::Out.WriteLine('{\"event\":\"hello\"}')\n$null = [Console]::ReadLine()\n" + finish;
        var start = new ProcessStartInfo("pwsh");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return new(start, log.Enqueue);
    }
    private static async Task Hello(ProcessEngineConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await connection.SendAsync("shutdown", "hello", null, timeout.Token);
        Assert.Equal("hello", (await connection.ReadAsync(timeout.Token))?.Text("event"));
    }
    [Fact]
    public async Task ShutdownDrainsLargeFinalOutputAndRepeatedDisposeSharesOneCleanup()
    {
        var log = new ConcurrentQueue<string>();
        await using var connection = Open("[Console]::Out.Write(('x' * 131072)); [Console]::Out.Flush()", log);
        await Hello(connection);
        await connection.SendAsync("shutdown", "stop", null, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        var first = connection.DisposeAsync().AsTask();
        var second = connection.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await first.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains(log, line => line.Contains("process-exit code=0"));
        Assert.DoesNotContain(log, line => line.Contains("deadline exceeded"));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1100)]
    public async Task AnUnresponsiveEngineSharesTheStopDeadlineWithDispose(int beforeDisposeMs)
    {
        var log = new ConcurrentQueue<string>();
        await using var connection = Open("[Threading.Thread]::Sleep(60000)", log);
        await Hello(connection);
        var watch = Stopwatch.StartNew();
        await connection.SendAsync("shutdown", "stop", null, CancellationToken.None);
        await Task.Delay(beforeDisposeMs);
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
        Assert.Single(log, line => line.Contains("deadline exceeded"));
    }
    [Fact]
    public async Task ShutdownCancelsAnOutstandingProtocolReadBeforeDraining()
    {
        var log = new ConcurrentQueue<string>();
        await using var connection = Open("[Console]::Out.Write(('x' * 131072)); [Console]::Out.Flush()", log);
        await Hello(connection);
        var pending = connection.ReadAsync(CancellationToken.None).AsTask();
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Contains(log, line => line.Contains("process-exit code=0"));
        Assert.DoesNotContain(log, line => line.Contains("deadline exceeded"));
    }
    [Fact]
    public async Task ShutdownInterruptsAWritingCommandWithoutWaitingForStdinToDrain()
    {
        var log = new ConcurrentQueue<string>();
        await using var connection = Open("[Threading.Thread]::Sleep(60000)", log);
        await Hello(connection);
        await connection.SendAsync("shutdown", "start", null, CancellationToken.None);
        var pending = connection.SendAsync("shutdown", "set_now_playing", new { text = new string('x', 131072) }, CancellationToken.None);
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        var failure = await Record.ExceptionAsync(() => pending);
        Assert.True(failure is OperationCanceledException or IOException or ObjectDisposedException, failure?.ToString());
        Assert.Single(log, line => line.Contains("deadline exceeded"));
    }
    [Fact]
    public async Task AFailedEngineExitStillReleasesItsPipes()
    {
        var log = new ConcurrentQueue<string>();
        await using var connection = Open("[Environment]::Exit(7)", log);
        await Hello(connection);
        await connection.SendAsync("shutdown", "stop", null, CancellationToken.None);
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Contains(log, line => line.Contains("process-exit code=7"));
    }
}
