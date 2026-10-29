using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DesktopSessionLogTests
{
    [Fact]
    public void AppendWritesOneHeaderAndOneRowPerSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"airflash-desktop-log-{Guid.NewGuid():N}");
        var path = Path.Combine(root, DesktopSessionLog.FileName);
        try
        {
            DesktopSessionLog.Append(path, DesktopSessionLog.FromFaults("abc", 0, []));
            DesktopSessionLog.Append(path, DesktopSessionLog.FromFaults("def", 2, ["timeline_expired", "engine_error"]));
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(DesktopSessionLog.Header, lines[0]);
            Assert.Equal("abc,0,none,0,none", lines[1]);
            Assert.Equal("def,2,timeline_expired,2,timeline_expired;engine_error", lines[2]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TheRecordSitsBesideTheEngineSummary()
    {
        // The engine writes session-summary.csv into this same directory (its
        // telemetry::summary_path), so the two files are joined without a path
        // lookup: same %APPDATA%\AirFlash\logs root, different file name.
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirFlash", "logs");
        Assert.Equal(Path.Combine(directory, DesktopSessionLog.FileName), DesktopSessionLog.DefaultPath);
        Assert.NotEqual("session-summary.csv", DesktopSessionLog.FileName);
        Assert.Equal("session_id,desktop_reconnects,transport_fault_first,transport_fault_count,transport_fault_seq", DesktopSessionLog.Header);
    }

    [Fact]
    public void AppendEscapesTheSessionIdAndNeverFailsTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"airflash-desktop-log-{Guid.NewGuid():N}");
        var path = Path.Combine(root, DesktopSessionLog.FileName);
        try
        {
            DesktopSessionLog.Append(path, DesktopSessionLog.FromFaults("a,b\"c", 1, []));
            var lines = File.ReadAllLines(path);
            Assert.Equal("\"a,b\"\"c\",1,none,0,none", lines[1]);
            // The quoted first field keeps the row parseable as five columns.
            Assert.StartsWith("\"a,b\"\"c\",", lines[1]);
            Assert.EndsWith(",1,none,0,none", lines[1]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TheFaultSequenceKeepsFiveEntriesAndCountsEveryFault()
    {
        string[] faults = ["timeout", "refused", "timeout", "timeline_expired", "clock_lost", "refused"];
        var record = DesktopSessionLog.FromFaults("abc", 6, faults);
        Assert.Equal("timeout", record.TransportFaultFirst);
        Assert.Equal(6, record.TransportFaultCount);
        Assert.Equal("timeout;refused;timeout;timeline_expired;clock_lost…", record.TransportFaultSeq);
        // A single fault is not more work to read than the old single reason.
        var single = DesktopSessionLog.FromFaults("abc", 1, ["refused"]);
        Assert.Equal("refused", single.TransportFaultFirst);
        Assert.Equal(1, single.TransportFaultCount);
        Assert.Equal("refused", single.TransportFaultSeq);
    }

    [Fact]
    public void AnUnwritableRecordPathIsSwallowed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"airflash-desktop-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // A regular file where the logs directory should be makes
            // Directory.CreateDirectory/AppendAllText fail deterministically.
            var blocker = Path.Combine(root, "logs");
            File.WriteAllText(blocker, "not a directory");
            DesktopSessionLog.Append(Path.Combine(blocker, DesktopSessionLog.FileName), DesktopSessionLog.FromFaults("abc", 0, []));
            Assert.False(File.Exists(Path.Combine(blocker, DesktopSessionLog.FileName)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
