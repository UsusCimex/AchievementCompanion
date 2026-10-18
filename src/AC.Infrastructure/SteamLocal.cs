using System.Runtime.Versioning;
using AC.Core.SteamFormats;
using Microsoft.Win32;

namespace AC.Infrastructure;

/// <summary>
/// Чтение локального Steam без сети и без доступа к процессу игры (раздел 2.2 ТЗ):
/// реестр, loginusers.vdf, appcache/stats.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamLocal
{
    public const ulong SteamId64Base = 76561197960265728UL;
    private const string SteamKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";

    public static string? SteamPath()
    {
        var p = Registry.GetValue(SteamKey, "SteamPath", null) as string;
        if (string.IsNullOrEmpty(p))
            p = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>AppID запущенной игры (Steam пишет его сам), 0 - ничего не запущено.</summary>
    public static uint RunningAppId()
    {
        var v = Registry.GetValue(SteamKey, "RunningAppID", 0);
        return v switch
        {
            int i when i > 0 => (uint)i,
            _ => 0,
        };
    }

    /// <summary>Язык клиента Steam (russian, english...).</summary>
    public static string? ClientLanguage() => Registry.GetValue(SteamKey, "Language", null) as string;

    public static (ulong SteamId, string PersonaName)? MostRecentUser()
    {
        var steam = SteamPath();
        if (steam is null) return null;
        var path = Path.Combine(steam, "config", "loginusers.vdf");
        if (!File.Exists(path)) return null;
        try
        {
            return TextVdf.MostRecentUser(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
    }

    public static string? StatsDir()
    {
        var steam = SteamPath();
        return steam is null ? null : Path.Combine(steam, "appcache", "stats");
    }

    /// <summary>Схема из кеша клиента: со скрытыми описаниями и флагом "защищено".</summary>
    public static IReadOnlyList<SchemaAchievement>? ReadSchema(uint appId, string language)
    {
        var dir = StatsDir();
        if (dir is null) return null;
        var kv = BinaryKeyValue.Load(Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin"));
        return kv is null ? null : StatsSchemaReader.Read(kv, appId, language);
    }

    public static uint AccountId(ulong steamId64) => (uint)(steamId64 - SteamId64Base);

    public static string UserStatsFileName(ulong steamId64, uint appId) => $"UserGameStats_{AccountId(steamId64)}_{appId}.bin";
}

/// <summary>
/// Следит за файлом UserGameStats_*_appid.bin: Steam переписывает его, когда меняется статистика игрока.
/// Сам файл не разбираем (формат не документирован), это только сигнал "пора обновить статус".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatsFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    public event Action? Changed;

    public StatsFileWatcher(ulong steamId64, uint appId)
    {
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        var dir = SteamLocal.StatsDir();
        if (dir is null || !Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, SteamLocal.UserStatsFileName(steamId64, appId))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        _watcher.Renamed += (_, _) => Kick();
    }

    public bool IsActive => _watcher is not null;

    // Steam пишет файл несколько раз подряд - ждём 3 секунды тишины.
    private void Kick() => _debounce.Change(3000, Timeout.Infinite);

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
