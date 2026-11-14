using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AirFlash.Core;

/// <summary>UI language selected once at startup; unsupported preferences fall back to English.</summary>
public static class L
{
    private static readonly Dictionary<string, string> ChineseSimplified = LoadStrings("AirFlash.Core.Strings.zh.json");
    private static readonly Dictionary<string, string> ChineseTraditional = LoadStrings("AirFlash.Core.Strings.zh-TW.json");
    private static readonly Dictionary<string, string> German = LoadStrings("AirFlash.Core.Strings.de.json");
    private static readonly Dictionary<string, string> Spanish = LoadStrings("AirFlash.Core.Strings.es.json");
    private static readonly Dictionary<string, string> French = LoadStrings("AirFlash.Core.Strings.fr.json");
    private static readonly Dictionary<string, string> Italian = LoadStrings("AirFlash.Core.Strings.it.json");
    private static readonly Dictionary<string, string> Portuguese = LoadStrings("AirFlash.Core.Strings.pt.json");
    private static readonly Dictionary<string, string> Japanese = LoadStrings("AirFlash.Core.Strings.ja.json");
    private static readonly Dictionary<string, string> Korean = LoadStrings("AirFlash.Core.Strings.ko.json");
    private static readonly Dictionary<string, string> Russian = LoadStrings("AirFlash.Core.Strings.ru.json");
    private static Dictionary<string, string> _strings = [];
    public static bool IsChinese { get; private set; }

    public static string SelectLanguage(IEnumerable<string> preferences)
    {
        foreach (var preference in preferences)
        {
            if (preference.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                preference.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                preference.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                preference.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            var language = preference.Split('-')[0];
            if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (language.Equals("de", StringComparison.OrdinalIgnoreCase)) return "de";
            if (language.Equals("es", StringComparison.OrdinalIgnoreCase)) return "es";
            if (language.Equals("fr", StringComparison.OrdinalIgnoreCase)) return "fr";
            if (language.Equals("it", StringComparison.OrdinalIgnoreCase)) return "it";
            if (language.Equals("pt", StringComparison.OrdinalIgnoreCase)) return "pt";
            if (language.Equals("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
            if (language.Equals("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
            if (language.Equals("ru", StringComparison.OrdinalIgnoreCase)) return "ru";
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
            "de"    => German,
            "es"    => Spanish,
            "fr"    => French,
            "it"    => Italian,
            "pt"    => Portuguese,
            "ja"    => Japanese,
            "ko"    => Korean,
            "ru"    => Russian,
            _       => []
        };
        var culture = CultureInfo.GetCultureInfo(lang switch
        {
            "zh-TW" => "zh-TW",
            "zh"    => "zh-CN",
            "de"    => "de-DE",
            "es"    => "es-ES",
            "fr"    => "fr-FR",
            "it"    => "it-IT",
            "pt"    => "pt-BR",
            "ja"    => "ja-JP",
            "ko"    => "ko-KR",
            "ru"    => "ru-RU",
            _       => "en-US"
        });
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Get(string english) => _strings.TryGetValue(english, out var translated) ? translated : english;
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
