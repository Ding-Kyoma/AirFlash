using AirFlash.App.ViewModels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class ErrorDetailsTextTests
{
    private const string Warning = "Synthetic discovery warning";
    private static readonly EngineNotice Fault = new("clock_lost", "", "clock", "Synthetic clock failure", true);

    [Theory]
    [InlineData(PlaybackState.Connecting, true)]
    [InlineData(PlaybackState.Error, false)]
    [InlineData(PlaybackState.Preempted, false)]
    public void IndependentNoticeDoesNotReplacePlaybackFault(PlaybackState state, bool retrying)
    {
        var snapshot = new SessionSnapshot(state, Message: "Status summary", Diagnostics: new(LastFault: Fault)) { IsRetrying = retrying };

        Assert.Equal(Fault.Detail + Environment.NewLine + Environment.NewLine + Warning, ErrorDetailsText.For(snapshot, Warning));
        Assert.Equal(Fault.Detail, ErrorDetailsText.For(snapshot, Fault.Detail));
    }

    [Theory]
    [InlineData(PlaybackState.Idle)]
    [InlineData(PlaybackState.Streaming)]
    [InlineData(PlaybackState.Standby)]
    [InlineData(PlaybackState.Pairing)]
    [InlineData(PlaybackState.Connecting)]
    public void OtherActivityDoesNotDisplayTheHistoricalFault(PlaybackState state)
    {
        var snapshot = new SessionSnapshot(state, Diagnostics: new(LastFault: Fault));
        Assert.Equal(Warning, ErrorDetailsText.For(snapshot, Warning));
    }

    [Theory]
    [InlineData(PlaybackState.Error)]
    [InlineData(PlaybackState.Preempted)]
    public void TerminalSummaryIsAvailableWithoutStructuredFault(PlaybackState state)
        => Assert.Equal("Synthetic terminal summary", ErrorDetailsText.For(new(state, Message: "Synthetic terminal summary"), ""));

    [Fact]
    public void WhitespaceDoesNotShowAnEmptyDetailsPanel()
        => Assert.Equal("", ErrorDetailsText.For(new(PlaybackState.Connecting, Message: "Connecting…"), " \t "));
}
