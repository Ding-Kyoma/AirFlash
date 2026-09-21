using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;
public class DeviceVolumeTests
{
    private static JsonElement Sample(int? volume, long sequence = 0, string status = "confirmed", bool available = true)
        => JsonSerializer.SerializeToElement(new { volume, sequence, status, available });
    [Fact]
    public void InitialStateIsUnknownAndPhysicalChangesAreReadBack()
    {
        var state = new DeviceVolumeState();
        Assert.Null(state.Display); Assert.False(state.Available);
        state = state.Receive(Sample(28)); Assert.Equal(28, state.Display);
        state = state.Receive(Sample(33)); Assert.Equal(33, state.Display); Assert.Equal(33, state.LastNonZero);
    }
    [Fact]
    public void OldQueriesCannotOverwriteLatestDragOrConfirmIt()
    {
        var state = new DeviceVolumeState().Receive(Sample(28)).Begin(45, 1).Begin(60, 2);
        Assert.Equal(state, state.Receive(Sample(28)));
        Assert.Equal(state, state.Receive(Sample(45, 1)));
        state = state.Receive(Sample(28, 2, "pending")); Assert.Equal(60, state.Display); Assert.Equal(28, state.Actual);
        state = state.Receive(Sample(60, 2)); Assert.Null(state.Target); Assert.Equal(60, state.Display);
    }
    [Fact]
    public void TimeoutUsesActualReadAndLatePendingDoesNotResurrectTarget()
    {
        var state = new DeviceVolumeState().Receive(Sample(28)).Begin(60, 1).Receive(Sample(35, 1, "pending")).Timeout();
        Assert.Equal(35, state.Display); Assert.Equal("unconfirmed", state.Status);
        state = state.Receive(Sample(38, 1, "pending")); Assert.Null(state.Target); Assert.Equal(38, state.Display);
    }
    [Fact]
    public void MissingReadsDisableControlWithoutInventingDefaultVolume()
    {
        var state = new DeviceVolumeState().Receive(Sample(null, status: "unsynced", available: false));
        Assert.Null(state.Display); Assert.False(state.Available);
        state = state.Receive(Sample(28)).Begin(60, 1).Timeout();
        Assert.False(state.Available); Assert.Null(state.Target);
    }
    [Fact]
    public void MuteKeepsLastConfirmedNonzeroAndNeverInventsRestoreValue()
    {
        var state = new DeviceVolumeState().Receive(Sample(0)); Assert.Null(state.LastNonZero);
        state = state.Receive(Sample(28)).Begin(0, 1).Receive(Sample(0, 1));
        Assert.Equal(0, state.Display); Assert.Equal(28, state.LastNonZero);
    }
}
