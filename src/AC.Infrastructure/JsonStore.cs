using System.Globalization;
using System.Text.Json;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// Локальное хранилище: по файлу JSON на игру, настройки, кеш библиотеки, сводка прогресса и журнал записей в Steam.
/// Папка по умолчанию - AppPaths.DataRoot (обычно %LocalAppData%\AchievementCompanion).
/// </summary>
public sealed class JsonStore
{
    private readonly object _logLock = new();
    private readonly object _summaryLock = new();
    private Dictionary<uint, GameSummary>? _summaries;

    public string Root { get; }
    private string GamesDir => Path.Combine(Root, "games");
    private string SettingsPath => Path.Combine(Root, "settings.json");
    private string LibraryPath => Path.Combine(Root, "library.json");
    private string SummaryPath => Path.Combine(Root, "summary.json");
    public string LogPath => Path.Combine(Root, "unlock_log.jsonl");

    public JsonStore(string? root = null)
    {
        Root = root ?? AppPaths.DataRoot;
        Directory.CreateDirectory(GamesDir);
    }

    public string GamePath(uint appId) => Path.Combine(GamesDir, appId.ToString(CultureInfo.InvariantCulture) + ".json");

    public GameData? LoadGame(uint appId) => Read<GameData>(GamePath(appId));

    public void SaveGame(GameData game)
    {
        Write(GamePath(game.AppId), game);
        UpdateSummary(game);
    }

    public AppSettings LoadSettings() => Read<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings s) => Write(SettingsPath, s);

    public List<OwnedGame>? LoadLibrary() => Read<List<OwnedGame>>(LibraryPath);

    public void SaveLibrary(List<OwnedGame> games) => Write(LibraryPath, games);

    /// <summary>Прогресс всех игр, которые открывались в приложении, - для библиотеки, без чтения файлов игр.</summary>
    public IReadOnlyDictionary<uint, GameSummary> LoadSummaries()
    {
        lock (_summaryLock) return new Dictionary<uint, GameSummary>(Summaries());
    }

    private Dictionary<uint, GameSummary> Summaries()
    {
        if (_summaries != null) return _summaries;
        _summaries = Read<Dictionary<uint, GameSummary>>(SummaryPath);
        if (_summaries is null)
        {
            // Сводки ещё нет: собираем её один раз из уже сохранённых игр.
            _summaries = new();
            foreach (var file in Directory.EnumerateFiles(GamesDir, "*.json"))
            {
                if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), out var appId)) continue;
                if (Read<GameData>(file) is { Achievements.Count: > 0 } g) _summaries[appId] = GameSummary.Of(g);
            }
            Write(SummaryPath, _summaries);
        }
        return _summaries;
    }

    private void UpdateSummary(GameData game)
    {
        if (game.Achievements.Count == 0) return;
        var fresh = GameSummary.Of(game);
        lock (_summaryLock)
        {
            var all = Summaries();
            if (all.TryGetValue(game.AppId, out var old) && old.Equals(fresh)) return;
            all[game.AppId] = fresh;
            Write(SummaryPath, all);
        }
    }

    public void AppendLog(UnlockLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, StoreJson.Compact);
        lock (_logLock) File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    public IReadOnlyList<UnlockLogEntry> ReadLog(uint? appId = null)
    {
        if (!File.Exists(LogPath)) return Array.Empty<UnlockLogEntry>();
        var list = new List<UnlockLogEntry>();
        lock (_logLock)
        {
            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<UnlockLogEntry>(line, StoreJson.Compact);
                    if (e != null && (appId is null || e.AppId == appId)) list.Add(e);
                }
                catch (JsonException) { /* битая строка - пропускаем */ }
            }
        }
        return list;
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, StoreJson.Pretty);
        }
        catch (JsonException)
        {
            // Файл повреждён: сохраняем копию, чтобы не потерять ручные отметки, и начинаем с чистого.
            try { File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>Запись через временный файл, чтобы при сбое не остался обрезанный JSON.</summary>
    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, value, StoreJson.Pretty);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class UnlockLogEntry
{
    public DateTimeOffset At { get; set; }
    public uint AppId { get; set; }
    public string ApiName { get; set; } = "";
    /// <summary>unlock или lock.</summary>
    public string Action { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>Прогресс игры для списка библиотеки.</summary>
public sealed record GameSummary
{
    public int Unlocked { get; init; }
    public int Manual { get; init; }
    public int Total { get; init; }

    public static GameSummary Of(GameData game)
    {
        var (u, m, total) = GameLogic.Progress(game);
        return new GameSummary { Unlocked = u, Manual = m, Total = total };
    }
}

public sealed class OwnedGame
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public int PlaytimeMinutes { get; set; }
    public string IconHash { get; set; } = "";
    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Шапка игры из магазина Steam (460x215).</summary>
    public string HeaderUrl => SteamImages.Header(AppId);

    public string? IconUrl => string.IsNullOrEmpty(IconHash) ? null :
        $"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg";
}

internal static class StoreJson
{
    public static readonly JsonSerializerOptions Pretty = new(JsonOptions.Pretty);
    public static readonly JsonSerializerOptions Compact = new(JsonOptions.Pretty) { WriteIndented = false };
}

public static class SteamImages
{
    /// <summary>Широкая картинка из библиотеки Steam (фон шапки).</summary>
    public static string Hero(uint appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg";

    public static string Header(uint appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
}
