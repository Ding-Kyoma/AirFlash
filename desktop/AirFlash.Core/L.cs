using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AirFlash.Core;

/// <summary>UI language selected once at startup; unsupported preferences fall back to English.</summary>
public static class L
{
    private static readonly Dictionary<string, string> Chinese = LoadChinese();
    public static bool IsChinese { get; private set; }

    public static string SelectLanguage(IEnumerable<string> preferences)
    {
        foreach (var preference in preferences)
        {
            var language = preference.Split('-')[0];
            if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase)) return "en";
        }
        return "en";
    }

    public static void Initialize(IEnumerable<string>? preferences = null)
    {
        IsChinese = SelectLanguage(preferences ?? WindowsPreferences()) == "zh";
        var culture = CultureInfo.GetCultureInfo(IsChinese ? "zh-CN" : "en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Get(string english) => IsChinese && Chinese.TryGetValue(english, out var chinese) ? chinese : english;
    public static string Format(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(english), args);

    private static Dictionary<string, string> LoadChinese()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static IEnumerable<string> WindowsPreferences()
    {
        if (OperatingSystem.IsWindows())
        {
            uint length = 0;
            if (GetUserPreferredUILanguages(8, out _, IntPtr.Zero, ref length) && length > 0)
            {
                var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
                try
                {
                    if (GetUserPreferredUILanguages(8, out _, buffer, ref length))
                        return (Marshal.PtrToStringUni(buffer, (int)length) ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return [];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, IntPtr languages, ref uint length);
}
