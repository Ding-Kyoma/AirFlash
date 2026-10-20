namespace AirFlash.Core;

public static class AppVersion
{
    public static string Normalize(string? informationalVersion, Version assemblyVersion)
    {
        var value = string.IsNullOrWhiteSpace(informationalVersion)
            ? assemblyVersion.ToString(3)
            : informationalVersion.Trim();
        var metadata = value.IndexOf('+');
        return metadata >= 0 ? value[..metadata] : value;
    }

    public static bool IsPreview(string? informationalVersion, Version assemblyVersion) =>
        Normalize(informationalVersion, assemblyVersion).Contains("-rc.", StringComparison.OrdinalIgnoreCase);

    public static bool IsDevelopment(string? informationalVersion, Version assemblyVersion) =>
        Normalize(informationalVersion, assemblyVersion).Contains("-dev.", StringComparison.OrdinalIgnoreCase);

    public static string Label(string? informationalVersion, Version assemblyVersion)
    {
        var display = Normalize(informationalVersion, assemblyVersion);
        var suffix = IsPreview(informationalVersion, assemblyVersion) ? " · Preview" : "";
        return $"AirFlash {display}{suffix} · WPF";
    }
}
