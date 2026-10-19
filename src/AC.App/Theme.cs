using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AC.Infrastructure;
using Microsoft.Win32;

namespace AC.App;

/// <summary>
/// Тема оформления: встроенная Fluent (светлая, тёмная или как в Windows) и своя палитра поверх неё -
/// цвета, которых во Fluent нет (редкость). Палитра подбирается под тему, которая реально применилась:
/// текст должен читаться и на белом, и на тёмном (контраст не ниже 4,5:1 по WCAG AA).
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
        // "Как в Windows": Fluent переключается сам, палитру догоняем после него.
        SystemEvents.UserPreferenceChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(UpdatePalette, DispatcherPriority.Background);
        _listening = true;
    }

    /// <summary>Тёмная ли тема сейчас - по фону, который выставил Fluent.</summary>
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
