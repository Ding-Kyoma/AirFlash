using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AirFlash.Core;

/// <summary>UI language selected once at startup; unsupported preferences fall back to English.</summary>
public static class L
{
    private static readonly Dictionary<string, string> ChineseSimplified = LoadStrings("AirFlash.Core.Strings.zh.json");
    private static readonly Dictionary<string, string> ChineseTraditional = LoadStrings("AirFlash.Core.Strings.zh-TW.json");
    private static Dictionary<string, string> _strings = [];
    public static bool IsChinese { get; private set; }

    public static string SelectLanguage(IEnumerable<string> preferences)
    {
        foreach (var preference in preferences)
        {
            if (preference.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                preference.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                preference.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            var language = preference.Split('-')[0];
            if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase)) return "en";
        }
        return "en";
    }

    public static void Initialize(IEnumerable<string>? preferences = null)
    {
        var lang = SelectLanguage(preferences ?? WindowsPreferences());
        IsChinese = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        _strings = lang switch
        {
            "zh-TW" => ChineseTraditional,
            "zh"    => ChineseSimplified,
            _       => []
        };
        var culture = CultureInfo.GetCultureInfo(lang switch
        {
            "zh-TW" => "zh-TW",
            "zh"    => "zh-CN",
            _       => "en-US"
        });
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Get(string english) => IsChinese && _strings.TryGetValue(english, out var translated) ? translated : english;
    public static string Format(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(english), args);

    private static Dictionary<string, string> LoadStrings(string resourceName)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream(resourceName)!;
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
