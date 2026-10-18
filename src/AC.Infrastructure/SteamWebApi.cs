using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AC.Infrastructure;

public sealed class SteamApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed record SchemaEntry(string ApiName, string Name, string Description, bool Hidden, string Icon, string IconGray);

public sealed record PlayerAchievement(string ApiName, bool Achieved, DateTimeOffset? UnlockTime);

/// <summary>
/// Клиент официального Steam Web API (раздел 2.1 ТЗ).
/// Лимит Valve - 100 000 запросов в сутки на ключ; приложение кеширует схему на сутки.
/// </summary>
public sealed class SteamWebApi(HttpClient http)
{
    private const string Base = "https://api.steampowered.com";

    public async Task<List<OwnedGame>> GetOwnedGamesAsync(string key, ulong steamId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{Base}/IPlayerService/GetOwnedGames/v1/?key={Esc(key)}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json", ct);
        var list = new List<OwnedGame>();
        if (!doc.RootElement.TryGetProperty("response", out var resp) || !resp.TryGetProperty("games", out var games))
            throw new SteamApiException("Steam не вернул библиотеку: профиль скрыт или ключ привязан к другому аккаунту");
        foreach (var g in games.EnumerateArray())
        {
            long last = g.TryGetProperty("rtime_last_played", out var lp) ? lp.GetInt64() : 0;
            list.Add(new OwnedGame
            {
                AppId = g.GetProperty("appid").GetUInt32(),
                Name = Str(g, "name"),
                PlaytimeMinutes = g.TryGetProperty("playtime_forever", out var pt) ? pt.GetInt32() : 0,
                IconHash = Str(g, "img_icon_url"),
                LastPlayed = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last) : null,
            });
        }
        return list;
    }

    public async Task<(string GameName, List<SchemaEntry> Achievements)> GetSchemaAsync(
        string key, uint appId, string language, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{Base}/ISteamUserStats/GetSchemaForGame/v2/?key={Esc(key)}&appid={appId}&l={Esc(language)}&format=json", ct);
        var list = new List<SchemaEntry>();
        if (!doc.RootElement.TryGetProperty("game", out var game)) return ("", list);
        var name = Str(game, "gameName");
        if (game.TryGetProperty("availableGameStats", out var stats) && stats.TryGetProperty("achievements", out var achs))
        {
            foreach (var a in achs.EnumerateArray())
            {
                list.Add(new SchemaEntry(
                    Str(a, "name"),
                    Str(a, "displayName"),
                    Str(a, "description"),
                    a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() != 0,
                    Str(a, "icon"),
                    Str(a, "icongray")));
            }
        }
        return (name, list);
    }

    /// <summary>Глобальные проценты. Ключ не нужен. Процент приходит то числом, то строкой - обрабатываем оба.</summary>
    public async Task<Dictionary<string, double>> GetGlobalPercentagesAsync(uint appId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{Base}/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}&format=json", ct);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("achievementpercentages", out var root) && root.TryGetProperty("achievements", out var achs))
        {
            foreach (var a in achs.EnumerateArray())
            {
                var name = Str(a, "name");
                if (!a.TryGetProperty("percent", out var p)) continue;
                double v = p.ValueKind switch
                {
                    JsonValueKind.Number => p.GetDouble(),
                    JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                    _ => double.NaN,
                };
                if (!double.IsNaN(v)) map[name] = v;
            }
        }
        return map;
    }

    public async Task<List<PlayerAchievement>> GetPlayerAchievementsAsync(
        string key, ulong steamId, uint appId, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(
                $"{Base}/ISteamUserStats/GetPlayerAchievements/v1/?key={Esc(key)}&steamid={steamId}&appid={appId}&format=json", ct,
                allowErrorBody: true);
        }
        catch (SteamApiException e) when (e.Status is HttpStatusCode.Forbidden)
        {
            throw new SteamApiException("Steam ответил 403: профиль скрыт (\"Сведения об играх\" в приватности Steam) или ключ неверный. Можно прочитать статус через помощник", e.Status);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("playerstats", out var ps))
                throw new SteamApiException("Пустой ответ GetPlayerAchievements");
            if (ps.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var err = Str(ps, "error");
                if (err.Contains("not public", StringComparison.OrdinalIgnoreCase))
                    throw new SteamApiException("Профиль скрыт: открой \"Сведения об играх\" в настройках приватности Steam или используй чтение через помощник", HttpStatusCode.Forbidden);
                throw new SteamApiException("Steam: " + (err.Length > 0 ? err : "ошибка GetPlayerAchievements"));
            }
            var list = new List<PlayerAchievement>();
            if (ps.TryGetProperty("achievements", out var achs))
            {
                foreach (var a in achs.EnumerateArray())
                {
                    bool achieved = a.TryGetProperty("achieved", out var ach) && ach.GetInt32() != 0;
                    long t = a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() : 0;
                    list.Add(new PlayerAchievement(Str(a, "apiname"), achieved,
                        achieved && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null));
                }
            }
            return list;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool allowErrorBody = false)
    {
        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden && !allowErrorBody)
            throw new SteamApiException("Steam отклонил ключ (401/403): проверь API-ключ в настройках", resp.StatusCode);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SteamApiException("Steam ограничил частоту запросов (429), попробуй позже", resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (allowErrorBody && body.TrimStart().StartsWith('{'))
            {
                try
                {
                    var errDoc = JsonDocument.Parse(body);
                    // тело с playerstats.error разберёт вызывающий
                    if (errDoc.RootElement.TryGetProperty("playerstats", out _)) return errDoc;
                    errDoc.Dispose();
                }
                catch (JsonException) { }
            }
            throw new SteamApiException($"Steam ответил {(int)resp.StatusCode}", resp.StatusCode);
        }
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new SteamApiException("Steam вернул не JSON");
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
