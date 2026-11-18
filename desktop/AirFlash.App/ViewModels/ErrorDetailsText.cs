using AirFlash.Core;

namespace AirFlash.App.ViewModels;

internal static class ErrorDetailsText
{
    public static string For(SessionSnapshot snapshot, string notice)
    {
        var recordedFault = snapshot.Diagnostics?.LastFault?.Detail;
        var fault = snapshot.State switch
        {
            PlaybackState.Error or PlaybackState.Preempted => string.IsNullOrWhiteSpace(recordedFault) ? snapshot.Message : recordedFault,
            PlaybackState.Connecting when snapshot.IsRetrying => recordedFault,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(fault)) return string.IsNullOrWhiteSpace(notice) ? "" : notice;
        if (string.IsNullOrWhiteSpace(notice) || notice == fault) return fault;
        return fault + Environment.NewLine + Environment.NewLine + notice;
    }
}
