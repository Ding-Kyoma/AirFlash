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
            DesktopSessionLog.Append(path, new DesktopSessionRecord("abc", 0, DesktopSessionLog.NoReconnect));
            DesktopSessionLog.Append(path, new DesktopSessionRecord("def", 2, "transport_fault:timeline_expired"));
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(DesktopSessionLog.Header, lines[0]);
            Assert.Equal("abc,0,none", lines[1]);
            Assert.Equal("def,2,transport_fault:timeline_expired", lines[2]);
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
        Assert.Equal("session_id,desktop_reconnects,desktop_reconnect_reason", DesktopSessionLog.Header);
    }

    [Fact]
    public void AppendEscapesTheSessionIdAndNeverFailsTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"airflash-desktop-log-{Guid.NewGuid():N}");
        var path = Path.Combine(root, DesktopSessionLog.FileName);
        try
        {
            DesktopSessionLog.Append(path, new DesktopSessionRecord("a,b\"c", 1, "none"));
            var lines = File.ReadAllLines(path);
            Assert.Equal("\"a,b\"\"c\",1,none", lines[1]);
            // The quoted first field keeps the row parseable as three columns.
            Assert.StartsWith("\"a,b\"\"c\",", lines[1]);
            Assert.EndsWith(",1,none", lines[1]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
            DesktopSessionLog.Append(Path.Combine(blocker, DesktopSessionLog.FileName), new DesktopSessionRecord("abc", 0, "none"));
            Assert.False(File.Exists(Path.Combine(blocker, DesktopSessionLog.FileName)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
