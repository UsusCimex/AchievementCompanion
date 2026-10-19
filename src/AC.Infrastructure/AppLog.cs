namespace AC.Infrastructure;

/// <summary>
/// App log: app.log in the data folder; past 1 MB the old one moves to app.log.1.
/// Written from any thread; failed writes are silently skipped because the log must not crash the app.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppPaths.DataRoot, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception e) => Write("ERROR", $"{context}: {e}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
