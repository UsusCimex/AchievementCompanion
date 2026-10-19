using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AC.Infrastructure;
using Microsoft.Win32;

namespace AC.App;

/// <summary>
/// Color theme: built-in Fluent (light, dark or as in Windows) with an own palette on top of it -
/// colors that Fluent lacks (rarity). The palette follows the theme actually applied:
/// text must read on both white and dark (contrast at least 4.5:1 per WCAG AA).
/// </summary>
internal static class ThemeManager
{
    private static bool _listening;

    public static void Apply(AppTheme theme)
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        UpdatePalette();
        if (_listening) return;
        // "As in Windows": Fluent switches itself, then the palette catches up.
        SystemEvents.UserPreferenceChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(UpdatePalette, DispatcherPriority.Background);
        _listening = true;
    }

    /// <summary>Whether the theme is dark, by the background Fluent set.</summary>
    public static bool IsDark =>
        Application.Current.TryFindResource("ApplicationBackgroundColor") is Color c && c.R + c.G + c.B < 3 * 128;

    private static void UpdatePalette()
    {
        bool dark = IsDark;
        var r = Application.Current.Resources;
        r["RarityVeryRareBrush"] = Brush(dark ? "#E3B341" : "#9A6700");
        r["RarityRareBrush"] = Brush(dark ? "#BC8CFF" : "#8250DF");
        r["RarityUncommonBrush"] = Brush(dark ? "#58A6FF" : "#0969DA");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
