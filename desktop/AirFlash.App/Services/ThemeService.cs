using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
namespace AirFlash.App.Services;

internal sealed record ThemePalette(
    bool IsDark,
    Color Canvas,
    Color Surface,
    Color Text,
    Color Muted,
    Color Line,
    Color Accent,
    Color Tint,
    Color Error,
    Color AccentText,
    Color AccentForeground,
    Color Caption,
    Color CaptionText)
{
    public static ThemePalette For(bool dark) => dark
        ? new(true, ColorOf("#141B27"), ColorOf("#202A3B"), ColorOf("#EEF3FC"), ColorOf("#AEBBD0"), ColorOf("#354157"), ColorOf("#78A9FF"), ColorOf("#253B5C"), ColorOf("#FF9AA5"), ColorOf("#78A9FF"), ColorOf("#162239"), ColorOf("#253B5C"), ColorOf("#EEF3FC"))
        : new(false, ColorOf("#F4F6FB"), ColorOf("#FFFFFF"), ColorOf("#162239"), ColorOf("#66748A"), ColorOf("#E0E6F0"), ColorOf("#2563EB"), ColorOf("#EAF1FF"), ColorOf("#C32D40"), ColorOf("#2563EB"), ColorOf("#FFFFFF"), ColorOf("#EAF1FF"), ColorOf("#162239"));

    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
}

public sealed class ThemeService : IDisposable
{
    internal static event Action<ThemePalette>? ThemeChanged;
    internal static ThemePalette CurrentPalette { get; private set; } = ThemePalette.For(false);
    private string _mode = "system";

    public ThemeService()
    {
        Refresh();
        SystemEvents.UserPreferenceChanged += Changed;
    }

    public void SetPreference(string mode)
    {
        _mode = mode;
        Refresh();
    }

    private void Changed(object sender, UserPreferenceChangedEventArgs args) { if (_mode == "system") Application.Current.Dispatcher.BeginInvoke(Refresh); }

    private void Refresh()
    {
        var dark = _mode == "dark";
        if (!dark && _mode == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        ApplyPalette(dark);
    }

    public static void ApplyPalette(bool dark)
    {
        var palette = ThemePalette.For(dark);
        CurrentPalette = palette;

        SetBrush("CanvasBrush", palette.Canvas);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("TextBrush", palette.Text);
        SetBrush("MutedBrush", palette.Muted);
        SetBrush("LineBrush", palette.Line);
        SetBrush("AccentBrush", palette.Accent);
        SetBrush("TintBrush", palette.Tint);
        SetBrush("ErrorBrush", palette.Error);
        SetBrush("AccentForegroundBrush", palette.AccentForeground);
        SetBrush("CaptionBrush", palette.Caption);
        SetBrush("CaptionTextBrush", palette.CaptionText);

        ApplyFluentResources(palette);
        ThemeChanged?.Invoke(palette);
    }

    public static void SetForVerification(bool dark)
    {
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        ApplyPalette(dark);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= Changed;

    private static void ApplyFluentResources(ThemePalette palette)
    {
        SetColorAndBrush("SystemColorWindowColor", palette.Canvas);
        SetColorAndBrush("SystemColorWindowTextColor", palette.Text);
        SetColorAndBrush("SystemColorButtonFaceColor", palette.Surface);
        SetColorAndBrush("SystemColorButtonTextColor", palette.Text);
        SetColorAndBrush("SystemColorHighlightColor", palette.Accent);
        SetColorAndBrush("SystemColorHighlightTextColor", palette.AccentForeground);
        SetColorAndBrush("SystemColorGrayTextColor", palette.Muted);
        SetColorAndBrush("SystemColorHotlightColor", palette.AccentText);

        foreach (var key in new[]
        {
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentFillColorSelectedTextBackgroundBrush", "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
            "CheckBoxCheckBackgroundFillIndeterminatePressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckBackgroundStrokeIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver",
            "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "RadioButtonOuterEllipseCheckedFill", "RadioButtonCheckOuterEllipseCheckedFillPointerOver",
            "RadioButtonCheckOuterEllipseCheckedFillPressed", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver",
            "ToggleButtonBackgroundCheckedPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver",
            "SliderThumbBackgroundPressed", "SliderOuterThumbBackground", "CalendarViewSelectedBackground",
            "CalendarViewSelectedBorderBrush", "ProgressBarForeground", "ProgressBarIndicatorAnimatedFill", "AccentControlElevationBorderBrush"
        }) SetBrush(key, palette.Accent);

        foreach (var key in new[]
        {
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
            "ListBoxItemSelectedForegroundThemeBrush", "CheckBoxCheckGlyphForeground", "RadioButtonCheckGlyphFill"
        }) SetBrush(key, palette.AccentText);

        foreach (var key in new[]
        {
            "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
            "CheckBoxCheckGlyphForegroundPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPressed"
        }) SetBrush(key, palette.AccentForeground);

        foreach (var key in new[]
        {
            "AccentFillColorDisabledBrush", "AccentButtonBackgroundDisabled", "AccentButtonBorderBrushDisabled",
            "CheckBoxCheckGlyphForegroundDisabled", "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonBorderBrushCheckedDisabled",
            "RadioButtonOuterEllipseCheckedFillDisabled"
        }) SetBrush(key, WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x28 : 0x37)));

        foreach (var key in new[]
        {
            "AccentTextFillColorDisabledBrush", "AccentButtonForegroundDisabled", "ToggleButtonForegroundCheckedDisabled"
        }) SetBrush(key, WithAlpha(palette.AccentForeground, (byte)(palette.IsDark ? 0x87 : 0x5C)));

        SetColorAndBrush("AccentFillColorDisabled", WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x28 : 0x37)));
        SetColorAndBrush("AccentTextFillColorDisabled", WithAlpha(palette.AccentForeground, (byte)(palette.IsDark ? 0x87 : 0x5C)));
        SetBrush("TextControlSelectionHighlightColor", palette.Accent);
        SetBrush("ListBoxItemSelectedBackgroundThemeBrush", palette.Tint);
        SetBrush("ListBoxItemSelectedBackgroundPointerOverThemeBrush", palette.Tint);
        SetBrush("ListBoxItemSelectedBackgroundPressedThemeBrush", palette.Tint);
        SetBrush("ProgressBarBackground", WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x8B : 0x37)));
        SetBrush("ProgressBarIndeterminateBackground", palette.Tint);
    }

    private static void SetColorAndBrush(string key, Color color)
    {
        Application.Current.Resources[key] = color;
        SetBrush(key + "Brush", color);
    }

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
