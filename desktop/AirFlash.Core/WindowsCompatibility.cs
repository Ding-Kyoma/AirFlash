using System.Runtime.InteropServices;
namespace AirFlash.Core;

public static class WindowsCompatibility
{
    public const int MinimumBuild = 17763;
    public const string Requirement = "Windows 10 version 1809 (build 17763) or later, x64";
    public static bool SupportsHost(Version version, Architecture process, Architecture system) =>
        version >= new Version(10, 0, MinimumBuild) && process == Architecture.X64 && system == Architecture.X64;
    public static bool SupportsWindowColors(Version version) => version >= new Version(10, 0, 22000);
}
