using System.Runtime.InteropServices;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class WindowsCompatibilityTests
{
    [Theory]
    [InlineData(17763, true, false)]
    [InlineData(19044, true, false)]
    [InlineData(19045, true, false)]
    [InlineData(22000, true, true)]
    [InlineData(17134, false, false)]
    public void BaselineAndOptionalWindowAttributesAreIndependent(int build, bool supported, bool colors)
    {
        var version = new Version(10, 0, build);
        Assert.Equal(supported, WindowsCompatibility.SupportsHost(version, Architecture.X64, Architecture.X64));
        Assert.Equal(colors, WindowsCompatibility.SupportsWindowColors(version));
    }
    [Fact]
    public void WrongArchitectureDoesNotQualify()
    {
        Assert.False(WindowsCompatibility.SupportsHost(new(10, 0, 19045), Architecture.X86, Architecture.X64));
        Assert.False(WindowsCompatibility.SupportsHost(new(10, 0, 26200), Architecture.X64, Architecture.Arm64));
    }
}
