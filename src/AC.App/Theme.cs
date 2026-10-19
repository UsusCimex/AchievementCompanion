using System.Windows;
using AC.Infrastructure;

namespace AC.App;

/// <summary>Тема оформления: встроенная Fluent - светлая, тёмная или как в Windows.</summary>
internal static class ThemeManager
{
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
    }
}
