using System.Windows;
using System.Windows.Threading;
using AC.Infrastructure;

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
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Error("Необработанная ошибка", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("Ошибка фоновой задачи", a.Exception);
            a.SetObserved();
        };
        AppLog.Info($"Запуск {typeof(App).Assembly.GetName().Version?.ToString(3)}, данные: {AppPaths.DataRoot}");
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Ошибка интерфейса", e.Exception);
        MessageBox.Show(e.Exception.Message, "Achievement Companion - ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
