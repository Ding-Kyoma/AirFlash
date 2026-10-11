using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void ProtectsJsonSecretsAndEscapedPathsWithoutCorruptingJson()
    {
        var json = JsonSerializer.Serialize(new { pin = 123456, private_key = "secret-material", password = "p\"ass", path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), clean = "startup failed" });
        using var value = JsonDocument.Parse(DiagnosticText.Protect(json));
        foreach (var label in new[] { "pin", "private_key", "password" }) Assert.Equal("[redacted]", value.RootElement.GetProperty(label).GetString());
        Assert.Equal("[user-path]", value.RootElement.GetProperty("path").GetString());
        Assert.Equal("startup failed", value.RootElement.GetProperty("clean").GetString());
    }

    [Fact]
    public void ProtectsLabelledStderr()
    {
        var safe = DiagnosticText.Protect("startup error pin=123456 public_key=abc password=\"secret value\"");
        Assert.DoesNotContain("123456", safe); Assert.DoesNotContain("abc", safe); Assert.DoesNotContain("secret value", safe);
        Assert.Contains("startup error", safe);
    }
}
