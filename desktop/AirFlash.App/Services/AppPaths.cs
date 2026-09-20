using AirFlash.Core;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
namespace AirFlash.App.Services;

public static class AppPaths
{
    public const string Name = "AirFlash";
    public static string Version => typeof(AppPaths).Assembly.GetName().Version!.ToString(3);
    public static string VersionLabel => $"AirFlash {Version} · WPF";
    public static string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static void Log(string message)
    {
        try { lock (LogLock) { Directory.CreateDirectory(LogDirectory); File.AppendAllText(Path.Combine(LogDirectory, $"wpf-{DateTime.Today:yyyy-MM-dd}.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static readonly object LogLock = new();
    internal static byte[] ReadEmbeddedEngine()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirFlash.Engine.exe")
            ?? throw new FileNotFoundException(L.Get("The Rust audio engine is missing. Rebuild the complete release package."));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
    public static string ExtractEngine()
    {
        var bytes = ReadEmbeddedEngine();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "engine", hash);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "airflash-engine.exe");
        if (File.Exists(target) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).Equals(hash, StringComparison.OrdinalIgnoreCase)) return target;
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }
    public static void OpenLogs() { Directory.CreateDirectory(LogDirectory); Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true }); }
}
