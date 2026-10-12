using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class EngineInitializationTests
{
    [Theory]
    [InlineData("engine missing")]
    [InlineData("cache access denied")]
    [InlineData("Win32 5: process rejected")]
    [InlineData("process exit 0xC0000139")]
    [InlineData("hello timeout")]
    [InlineData("incompatible protocol")]
    public async Task FailedProbeIsVisibleAndCanBeRetried(string message)
    {
        var attempts = 0;
        await using var engine = new EngineInitialization(_ => ++attempts == 1
            ? Task.FromException<string>(new IOException(message)) : Task.FromResult("0.1.0"));
        await engine.InitializeAsync();
        Assert.Equal(EngineAvailability.Failed, engine.State); Assert.Contains(message, engine.Error);
        await engine.InitializeAsync();
        Assert.Equal(EngineAvailability.Ready, engine.State); Assert.Equal("0.1.0", engine.Version);
        Assert.Equal("", engine.Error);
    }
    [Fact]
    public async Task SimultaneousRetryOnlyStartsOneProbe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var engine = new EngineInitialization(_ => { Interlocked.Increment(ref attempts); entered.SetResult(); return release.Task; });
        var first = engine.InitializeAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = engine.InitializeAsync();
        Assert.Same(first, second); Assert.Equal(EngineAvailability.Initializing, engine.State);
        release.SetResult("0.1.0"); await Task.WhenAll(first, second);
        Assert.Equal(1, attempts);
    }
    [Fact]
    public async Task ShutdownCancelsProbeAndRejectsLateReadyAndRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var engine = new EngineInitialization(token =>
        {
            Interlocked.Increment(ref attempts); token.Register(() => cancelled.SetResult()); entered.SetResult(); return release.Task;
        });
        var first = engine.InitializeAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = engine.DisposeAsync().AsTask(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult("late"); await Task.WhenAll(first, dispose);
        Assert.NotEqual(EngineAvailability.Ready, engine.State);
        await engine.InitializeAsync(); Assert.Equal(1, attempts);
    }
}
