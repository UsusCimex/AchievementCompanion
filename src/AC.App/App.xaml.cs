using System.Windows;
using System.Windows.Threading;
using AC.Infrastructure;

namespace AC.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Error("Необработанная ошибка", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("Ошибка фоновой задачи", a.Exception);
            a.SetObserved();
        };
        AppLog.Info($"Запуск {typeof(App).Assembly.GetName().Version?.ToString(3)}, данные: {AppPaths.DataRoot}");

        // Тема (встроенная Fluent): ставим до создания окон, чтобы её словарь был в ресурсах приложения.
        ThemeManager.Apply(new JsonStore().LoadSettings().Theme);
        ImageCache.TrimInBackground();
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Ошибка интерфейса", e.Exception);
        MessageBox.Show(e.Exception.Message, "Achievement Companion - ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
