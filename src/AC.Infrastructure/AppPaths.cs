namespace AC.Infrastructure;

/// <summary>
/// Where the app data lives, in order:
/// 1) AC_DATA_DIR environment variable - tests, screenshots, other profiles;
/// 2) data folder next to the exe - portable mode (e.g. a USB stick);
/// 3) %LocalAppData%\AchievementCompanion.
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "AC_DATA_DIR";

    public static string DataRoot { get; }

    /// <summary>Data is beside the exe (data folder).</summary>
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
