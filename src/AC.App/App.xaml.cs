using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Одна копия на папку данных: две копии спорили бы за горячие клавиши и файлы игр.
        var name = "AchievementCompanion." +
                   Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(AppPaths.DataRoot.ToLowerInvariant())))[..16];
        _single = new Mutex(true, name, out bool first);
        if (!first)
        {
            _single.Dispose();
            _single = null;
            ActivateRunningCopy(name);
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

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
        // Hardware rendering stops while the display is off; scripted screenshots switch to software rendering.
        if (Environment.GetEnvironmentVariable("AC_SOFTWARE_RENDERING") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var settings = new JsonStore().LoadSettings();
        Loc.SetLanguage(settings.UiLanguage);
        ThemeManager.Apply(settings.Theme);
        ImageCache.TrimInBackground();
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_single is not null)
        {
            try { _single.ReleaseMutex(); } catch (ApplicationException) { }
            _single.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Вторая копия не запускается, а просит первую показать окно. Показывает первая сама:
    /// окно своего процесса надёжнее поднять изнутри, чем искать его снаружи.
    /// </summary>
    private static void ActivateRunningCopy(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name + ".show", out var show)) return;
        using (show)
        {
            Native.AllowAnyForeground();
            show.Set();
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Ошибка интерфейса", e.Exception);
        MessageBox.Show(e.Exception.Message, Loc.T("dialog.errorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
