using AirFlash.App.Services;
using Xunit;

namespace AirFlash.Tests;

public class TrayRegistrationTests
{
    private sealed class Shell
    {
        public List<(TrayOperation Operation, TrayIdentity Identity)> Calls { get; } = [];
        public Queue<bool> Results { get; } = [];
        public bool Notify(TrayOperation operation, TrayIdentity identity)
        {
            Calls.Add((operation, identity));
            return Results.Count == 0 || Results.Dequeue();
        }
    }

    [Fact]
    public void ExistingGuidIsPreservedAndSuccessfulRegistrationIsNotRepeated()
    {
        var shell = new Shell();
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister());
        Assert.True(tray.TryRegister());
        Assert.True(tray.IsRegistered);
        Assert.True(tray.UsesVersion4);
        Assert.Equal(TrayIdentity.Guid, tray.Identity);
        Assert.Equal(new[] { (TrayOperation.Add, TrayIdentity.Guid), (TrayOperation.SetVersion, TrayIdentity.Guid) }, shell.Calls);
    }

    [Fact]
    public void RejectedGuidFallsBackAndKeepsWindowIdentityForUpdatesRestartAndDelete()
    {
        var shell = new Shell(); shell.Results.Enqueue(false);
        var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister());
        Assert.Equal(TrayIdentity.WindowIconId, tray.Identity);
        Assert.True(tray.Update());
        Assert.True(tray.TaskbarCreated());
        tray.Dispose();
        Assert.Equal(new[] {
            (TrayOperation.Add, TrayIdentity.Guid),
            (TrayOperation.Add, TrayIdentity.WindowIconId),
            (TrayOperation.SetVersion, TrayIdentity.WindowIconId),
            (TrayOperation.Modify, TrayIdentity.WindowIconId),
            (TrayOperation.Add, TrayIdentity.WindowIconId),
            (TrayOperation.SetVersion, TrayIdentity.WindowIconId),
            (TrayOperation.Delete, TrayIdentity.WindowIconId)
        }, shell.Calls);
    }

    [Fact]
    public void FailedAttemptsRemainRetryableAndDoNotSetVersionOrSelectIdentity()
    {
        var shell = new Shell();
        foreach (var result in new[] { false, false, false, false, true, true }) shell.Results.Enqueue(result);
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.False(tray.TryRegister());
        Assert.False(tray.TryRegister());
        Assert.False(tray.IsRegistered);
        Assert.False(tray.UsesVersion4);
        Assert.True(tray.TryRegister());
        Assert.Equal(TrayIdentity.Guid, tray.Identity);
        Assert.Equal(5, shell.Calls.Count(c => c.Operation == TrayOperation.Add));
        Assert.Single(shell.Calls, c => c.Operation == TrayOperation.SetVersion);
    }

    [Fact]
    public void FailedUpdateDeletesSurvivingEntryBeforeRegisteringAgain()
    {
        var shell = new Shell();
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister()); shell.Calls.Clear(); shell.Results.Enqueue(false);
        Assert.True(tray.Update());
        Assert.Equal(new[] {
            (TrayOperation.Modify, TrayIdentity.Guid), (TrayOperation.Delete, TrayIdentity.Guid),
            (TrayOperation.Add, TrayIdentity.Guid), (TrayOperation.SetVersion, TrayIdentity.Guid)
        }, shell.Calls);
    }

    [Fact]
    public void RestartFailuresDoNotSwitchAnAlreadySelectedIdentity()
    {
        var shell = new Shell();
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister()); shell.Calls.Clear(); shell.Results.Enqueue(false); shell.Results.Enqueue(false);
        Assert.False(tray.TaskbarCreated());
        Assert.False(tray.IsRegistered);
        Assert.True(tray.TryRegister());
        Assert.All(shell.Calls, call => Assert.Equal(TrayIdentity.Guid, call.Identity));
        Assert.Single(shell.Calls, c => c.Operation == TrayOperation.SetVersion);
    }

    [Fact]
    public void RepeatedTaskbarNotificationUpdatesExistingEntryWithoutSwitchingIdentity()
    {
        var shell = new Shell();
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister()); shell.Calls.Clear(); shell.Results.Enqueue(false);
        Assert.True(tray.TaskbarCreated());
        Assert.True(tray.IsRegistered);
        Assert.Equal(new[] { (TrayOperation.Add, TrayIdentity.Guid), (TrayOperation.Modify, TrayIdentity.Guid), (TrayOperation.SetVersion, TrayIdentity.Guid) }, shell.Calls);
    }

    [Fact]
    public void FailedVersionNegotiationLeavesTheIconRegisteredWithLegacyCallbacks()
    {
        var shell = new Shell(); shell.Results.Enqueue(true); shell.Results.Enqueue(false);
        using var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister());
        Assert.True(tray.IsRegistered);
        Assert.False(tray.UsesVersion4);
        Assert.Equal(TrayAction.TogglePanel, TrayRegistration.DecodeCallback(1, 0x202, tray.UsesVersion4));
        Assert.Equal(TrayAction.OpenMenu, TrayRegistration.DecodeCallback(1, 0x205, tray.UsesVersion4));
    }

    [Fact]
    public void DisposeIsIdempotentAndPreventsAllFurtherRegistration()
    {
        var shell = new Shell();
        var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.True(tray.TryRegister()); tray.Dispose(); var count = shell.Calls.Count;
        tray.Dispose();
        Assert.False(tray.TryRegister());
        Assert.False(tray.Update());
        Assert.False(tray.TaskbarCreated());
        Assert.False(tray.IsRegistered);
        Assert.Equal(count, shell.Calls.Count);
        Assert.Single(shell.Calls, c => c.Operation == TrayOperation.Delete);
    }

    [Fact]
    public void DisposeDuringFailedRegistrationDoesNotDeleteAnotherIconOrRetry()
    {
        var shell = new Shell(); shell.Results.Enqueue(false); shell.Results.Enqueue(false);
        var tray = new TrayRegistration(shell.Notify, _ => { });
        Assert.False(tray.TryRegister()); tray.Dispose();
        Assert.False(tray.TryRegister());
        Assert.DoesNotContain(shell.Calls, c => c.Operation == TrayOperation.Delete);
        Assert.Equal(2, shell.Calls.Count);
    }

    [Fact]
    public void RepeatedFailuresAreLoggedAtMostEveryThirtySecondsPerOperationAndIdentity()
    {
        var now = DateTimeOffset.UtcNow; var logs = new List<string>();
        var tray = new TrayRegistration((_, _) => false, logs.Add, () => now);
        Assert.False(tray.TryRegister()); Assert.Equal(2, logs.Count);
        now += TimeSpan.FromSeconds(2); Assert.False(tray.TryRegister()); Assert.Equal(2, logs.Count);
        now += TimeSpan.FromSeconds(28); Assert.False(tray.TryRegister()); Assert.Equal(4, logs.Count);
        tray.Dispose();
    }

    [Theory]
    [InlineData(0, 0x10400, true, 1)]
    [InlineData(0, 0x10401, true, 1)]
    [InlineData(0, 0x1007b, true, 2)]
    [InlineData(1, 0x202, false, 1)]
    [InlineData(1, 0x205, false, 2)]
    [InlineData(1, 0x7b, false, 2)]
    [InlineData(2, 0x202, false, 0)]
    [InlineData(1, 0x201, false, 0)]
    [InlineData(1, 0x203, false, 0)]
    [InlineData(0, 0x10200, true, 0)]
    public void CallbackDecodingSupportsBothVersions(long wParam, long lParam, bool version4, int expected)
        => Assert.Equal((TrayAction)expected, TrayRegistration.DecodeCallback(wParam, lParam, version4));
}
