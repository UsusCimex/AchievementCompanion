namespace AC.Infrastructure;

/// <summary>
/// Где лежат данные приложения. По порядку:
/// 1) переменная окружения AC_DATA_DIR - тесты, скриншоты, отдельный профиль;
/// 2) папка data рядом с exe - портативный режим (например, на флешке);
/// 3) %LocalAppData%\AchievementCompanion.
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "AC_DATA_DIR";

    public static string DataRoot { get; }

    /// <summary>Данные лежат рядом с exe (папка data).</summary>
    public static bool IsPortable { get; }

    static AppPaths()
    {
        var env = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            DataRoot = Path.GetFullPath(env);
            return;
        }
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(portable))
        {
            DataRoot = portable;
            IsPortable = true;
            return;
        }
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementCompanion");
    }
}
