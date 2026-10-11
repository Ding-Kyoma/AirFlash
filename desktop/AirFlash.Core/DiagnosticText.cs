using System.Text.RegularExpressions;
namespace AirFlash.Core;

public static class DiagnosticText
{
    public static string Protect(string text)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0) continue;
            text = text.Replace(path.Replace("\\", "\\\\"), "[user-path]", StringComparison.OrdinalIgnoreCase)
                .Replace(path, "[user-path]", StringComparison.OrdinalIgnoreCase);
        }
        const string labels = @"(?:pin|password|secret|token|credentials|(?:private_|public_|session_)?key|pairing_pin)";
        text = Regex.Replace(text, "(?i)(\"" + labels + @"""\s*:\s*)(""(?:\\.|[^""\\])*""|[0-9]+)", "$1\"[redacted]\"");
        return Regex.Replace(text, @"(?i)\b(" + labels + @")\s*[:=]\s*(""[^""\r\n]*""|[^\s,;]+)", "$1=[redacted]");
    }
}
