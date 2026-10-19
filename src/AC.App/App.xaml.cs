using System.Windows;
using System.Windows.Threading;

namespace AC.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Встроенная тема Fluent: светлая/тёмная по настройкам Windows, системный акцентный цвет.
        // Ставим в коде, а не в App.xaml, чтобы словарь темы добавился к уже загруженным ресурсам приложения
        // и был доступен окнам как StaticResource.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Achievement Companion - ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
