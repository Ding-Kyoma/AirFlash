using System.Runtime.InteropServices;
using AirFlash.Core;
namespace AirFlash.App.Services;

public sealed record WindowsCapabilities(Version OsVersion, Architecture ProcessArchitecture, Architecture OsArchitecture, bool DnsSdAvailable)
{
    public static readonly string[] DnsExports = ["DnsServiceBrowse", "DnsServiceResolve", "DnsServiceBrowseCancel", "DnsServiceResolveCancel", "DnsRecordListFree", "DnsServiceFreeInstance"];
    public bool Supported => WindowsCompatibility.SupportsHost(OsVersion, ProcessArchitecture, OsArchitecture);
    public bool WindowColors => WindowsCompatibility.SupportsWindowColors(OsVersion);
    public static WindowsCapabilities Current { get; } = Detect();
    private static WindowsCapabilities Detect() => new(Environment.OSVersion.Version,
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, HasExports("dnsapi.dll", DnsExports));
    private static bool HasExports(string library, IEnumerable<string> exports)
    {
        if (!NativeLibrary.TryLoad(library, typeof(WindowsCapabilities).Assembly, DllImportSearchPath.System32, out var module)) return false;
        try { return exports.All(name => NativeLibrary.TryGetExport(module, name, out _)); }
        finally { NativeLibrary.Free(module); }
    }
}
