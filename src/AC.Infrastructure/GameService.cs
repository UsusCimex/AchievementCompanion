using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

public sealed record LoadReport(GameData Game, IReadOnlyList<string> NewlyUnlocked, IReadOnlyList<string> Warnings);

public sealed record MarkReport(bool SteamWritten, string? Error);

/// <summary>
/// Сценарии приложения: загрузить игру, обновить статус, отметить, досчитать в Steam.
/// Все изменения сразу сохраняются на диск.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameService
{
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(24);

    private readonly JsonStore _store;
    private readonly SteamWebApi _api;
    private readonly BridgeClient _bridge;
    private readonly CompletionTimeSource _completion;

    public AppSettings Settings { get; private set; }
    public JsonStore Store => _store;
    public BridgeClient Bridge => _bridge;

    public GameService(JsonStore store, SteamWebApi api, BridgeClient bridge, CompletionTimeSource completion)
    {
        _store = store;
        _api = api;
        _bridge = bridge;
        _completion = completion;
        Settings = store.LoadSettings();
        if (Settings.SteamId64 == 0 && SteamLocal.MostRecentUser() is { } u)
        {
            Settings.SteamId64 = u.SteamId;
            _store.SaveSettings(Settings);
        }
    }

    public static GameService CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return new GameService(new JsonStore(), new SteamWebApi(http), new BridgeClient(), new CompletionTimeSource(http));
    }

    public string ApiKey
    {
        get => Dpapi.Unprotect(Settings.ApiKeyProtected);
        set => Settings.ApiKeyProtected = Dpapi.Protect(value.Trim());
    }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    public void SaveSettings() => _store.SaveSettings(Settings);

    // ---------- библиотека ----------

    public List<OwnedGame> CachedLibrary() => _store.LoadLibrary() ?? new List<OwnedGame>();

    public async Task<List<OwnedGame>> RefreshLibraryAsync(CancellationToken ct = default)
    {
        RequireKey();
        var games = await _api.GetOwnedGamesAsync(ApiKey, Settings.SteamId64, ct);
        games = games.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(g => g.Name).ToList();
        _store.SaveLibrary(games);
        return games;
    }

    // ---------- игра ----------

    public GameData? Cached(uint appId) => _store.LoadGame(appId);

    /// <summary>
    /// Загружает игру: схема (из кеша, если свежая), глобальные %, локальная схема Steam,
    /// статус игрока из Web API. Ошибки отдельных шагов не валят загрузку, а попадают в Warnings.
    /// </summary>
    public async Task<LoadReport> LoadGameAsync(uint appId, string? knownName = null, bool forceSchema = false, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var game = _store.LoadGame(appId) ?? new GameData { AppId = appId, Name = knownName ?? "" };
        if (!string.IsNullOrEmpty(knownName) && string.IsNullOrEmpty(game.Name)) game.Name = knownName;

        var lib = CachedLibrary().FirstOrDefault(g => g.AppId == appId);
        if (lib != null) game.PlaytimeMinutes = lib.PlaytimeMinutes;

        bool stale = forceSchema || game.Achievements.Count == 0 || game.SchemaFetchedAt is null ||
                     DateTimeOffset.Now - game.SchemaFetchedAt > SchemaTtl ||
                     !string.Equals(game.SchemaLanguage, Settings.Language, StringComparison.OrdinalIgnoreCase);

        if (stale)
        {
            if (HasApiKey)
            {
                try
                {
                    var (name, entries) = await _api.GetSchemaAsync(ApiKey, appId, Settings.Language, ct);
                    if (!string.IsNullOrEmpty(name)) game.Name = name;
                    MergeSchema(game, entries);
                    game.SchemaFetchedAt = DateTimeOffset.Now;
                    game.SchemaLanguage = Settings.Language;
                }
                catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
                {
                    warnings.Add("Схема: " + e.Message);
                }
            }
            else warnings.Add("Нет API-ключа: схема только из локального кеша Steam");
        }

        // Локальная схема даёт описания скрытых и флаг "защищено"; дешёвая, читаем всегда.
        MergeLocalSchema(game);

        // Проценты - после того, как список достижений собран из всех источников
        // (раньше без ключа они запрашивались до локальной схемы и терялись).
        if (stale || game.Achievements.Any(a => a.GlobalPercent is null))
        {
            var err = await FetchPercentagesAsync(game, ct);
            if (err != null) warnings.Add("Глобальные %: " + err);
        }

        IReadOnlyList<string> fresh = Array.Empty<string>();
        if (Settings.StatusSource == StatusSource.WebApi && HasApiKey && Settings.SteamId64 != 0)
        {
            try
            {
                fresh = await RefreshStateAsync(game, ct);
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
            {
                warnings.Add("Статус: " + e.Message);
            }
        }

        GameLogic.AutoClassify(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
        Settings.LastAppId = appId;
        SaveSettings();
        return new LoadReport(game, fresh, warnings);
    }

    /// <summary>Статус из Web API. Возвращает только что открывшиеся.</summary>
    public async Task<IReadOnlyList<string>> RefreshStateAsync(GameData game, CancellationToken ct = default)
    {
        RequireKey();
        var list = await _api.GetPlayerAchievementsAsync(ApiKey, Settings.SteamId64, game.AppId, ct);
        var fresh = GameLogic.ApplySteamState(game, list.Select(a => (a.ApiName, a.Achieved, a.UnlockTime)), DateTimeOffset.Now);
        game.PendingUnlocks.RemoveAll(n => game.StateOf(n).SteamUnlocked);
        _store.SaveGame(game);
        return fresh;
    }

    /// <summary>
    /// Автообновление статуса: из выбранного источника. Без ключа Web API - всегда через Steam-клиент.
    /// Возвращает только что открывшиеся (их статус уже "открыто", ручные отметки закрываются сами).
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshStateAutoAsync(GameData game, CancellationToken ct = default)
    {
        bool useClient = Settings.StatusSource == StatusSource.SteamClient || !HasApiKey || Settings.SteamId64 == 0;
        if (!useClient) return await RefreshStateAsync(game, ct);
        var (fresh, error) = await RefreshViaBridgeAsync(game, ct);
        if (error != null) throw new SteamApiException("Steam-клиент: " + error);
        return fresh;
    }

    /// <summary>Своё "реально наиграно" (null - брать из Steam) и сколько часов Steam не считать.</summary>
    public void SetPlaytime(GameData game, double? overrideMinutes, double offsetMinutes)
    {
        game.PlaytimeOverrideMinutes = overrideMinutes is >= 0 ? overrideMinutes : null;
        game.PlaytimeOffsetMinutes = Math.Max(0, offsetMinutes);
        _store.SaveGame(game);
    }

    /// <summary>Статус и схема через помощник (работает при скрытом профиле, на секунду показывает "в игре").</summary>
    public async Task<(IReadOnlyList<string> Fresh, string? Error)> RefreshViaBridgeAsync(GameData game, CancellationToken ct = default)
    {
        var r = await _bridge.ReadAsync(game.AppId, Settings.Language, ct);
        if (!r.Ok || r.Achievements is null) return (Array.Empty<string>(), r.Error ?? "ошибка помощника");

        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        foreach (var a in r.Achievements)
        {
            if (!byName.TryGetValue(a.ApiName, out var def))
            {
                def = new AchievementDef { ApiName = a.ApiName };
                game.Achievements.Add(def);
                byName[a.ApiName] = def;
            }
            if (!string.IsNullOrEmpty(a.Name)) def.Name = a.Name;
            if (!string.IsNullOrEmpty(a.Description)) def.Description = a.Description;
            def.Hidden = a.Hidden;
            def.Protected = a.Protected;
            if (!string.IsNullOrEmpty(a.Icon)) def.IconUrl = a.Icon;
            if (!string.IsNullOrEmpty(a.IconGray)) def.IconGrayUrl = a.IconGray;
        }
        var fresh = GameLogic.ApplySteamState(game,
            r.Achievements.Select(a => (a.ApiName, a.Achieved, a.UnlockTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(a.UnlockTime) : (DateTimeOffset?)null)),
            DateTimeOffset.Now);
        game.PendingUnlocks.RemoveAll(n => game.StateOf(n).SteamUnlocked);
        if (game.Achievements.Any(a => a.GlobalPercent is null)) await FetchPercentagesAsync(game, ct);
        GameLogic.AutoClassify(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
        return (fresh, null);
    }

    /// <summary>
    /// Своя статистика по выборке игроков: берём авторов обзоров игры, читаем их достижения
    /// (у кого открыт профиль) и считаем проценты и время до каждого достижения.
    /// Нужен API-ключ. Около сотни запросов, обычно 20-60 секунд.
    /// </summary>
    public async Task<SampleResult> CollectSampleAsync(GameData game, IProgress<string>? progress = null,
        int maxAuthors = 300, int targetPlayers = 80, CancellationToken ct = default)
    {
        RequireKey();
        var key = ApiKey;
        progress?.Report("Ищу игроков по обзорам Steam...");
        var authors = await _api.GetReviewAuthorsAsync(game.AppId, maxAuthors, ct);
        authors = authors.Where(a => a.SteamId != Settings.SteamId64).ToList();
        if (authors.Count == 0) throw new SteamApiException("У игры нет обзоров в Steam - не из кого собрать выборку");

        var samples = new List<PlayerSample>();
        int checkedCount = 0;
        using var gate = new SemaphoreSlim(4);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = authors.Select(async a =>
        {
            try { await gate.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (stop.IsCancellationRequested) return;
                var list = await _api.GetPlayerAchievementsAsync(key, a.SteamId, game.AppId, stop.Token);
                var unlocks = list.Where(x => x.Achieved && x.UnlockTime is not null)
                                  .ToDictionary(x => x.ApiName, x => x.UnlockTime!.Value, StringComparer.Ordinal);
                var sample = new PlayerSample { SteamId = a.SteamId, Unlocks = unlocks };
                if (a.ReviewedAt is { } rv && a.PlaytimeAtReview > 0) sample.Anchors.Add((rv, a.PlaytimeAtReview));
                if (a.LastPlayed is { } lp && a.PlaytimeForever > 0) sample.Anchors.Add((lp, a.PlaytimeForever));
                bool enough;
                lock (samples)
                {
                    samples.Add(sample);
                    enough = samples.Count >= targetPlayers;
                }
                if (enough) stop.Cancel();
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // скрытый профиль или сбой - пропускаем игрока
            }
            finally
            {
                var c = Interlocked.Increment(ref checkedCount);
                if (c % 10 == 0) progress?.Report($"Проверено профилей: {c} из {authors.Count}, открытых: {samples.Count}");
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        if (samples.Count == 0)
            throw new SteamApiException($"Проверено {authors.Count} авторов обзоров - у всех скрыты сведения об играх");

        // Только считаем: применяет вызывающий к актуальному объекту игры (пока шёл сбор, её могли перезагрузить).
        return SampleStats.Compute(samples, game.Achievements.Select(a => a.ApiName).ToList());
    }

    public void ApplySample(GameData game, SampleResult r)
    {
        SampleStats.Apply(game, r, DateTimeOffset.Now);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
    }

    /// <summary>Глобальные проценты (ключ не нужен). Возвращает текст ошибки или null.</summary>
    private async Task<string?> FetchPercentagesAsync(GameData game, CancellationToken ct)
    {
        if (game.Achievements.Count == 0) return null;
        try
        {
            var pct = await _api.GetGlobalPercentagesAsync(game.AppId, ct);
            if (pct.Count == 0) return "Steam не отдал проценты для этой игры";
            foreach (var a in game.Achievements)
                if (pct.TryGetValue(a.ApiName, out var v)) a.GlobalPercent = v;
            return null;
        }
        catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Записать в Steam прямо сейчас, как в SAM: независимо от режима в настройках.
    /// Заодно ставит ручную отметку "сделал". Защищённые и уже открытые пропускаются.
    /// </summary>
    public async Task<MarkReport> UnlockInSteamAsync(GameData game, IReadOnlyList<string> apiNames, CancellationToken ct = default)
    {
        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        var skipped = new List<string>();
        var toWrite = new List<string>();
        foreach (var n in apiNames.Distinct(StringComparer.Ordinal))
        {
            if (game.StateOf(n).SteamUnlocked) continue;
            if (byName.TryGetValue(n, out var d) && d.Protected) { skipped.Add(d.Name.Length > 0 ? d.Name : n); continue; }
            var s = game.StateOf(n);
            if (!s.ManualDone) { s.ManualDone = true; s.ManualAt = DateTimeOffset.Now; }
            toWrite.Add(n);
        }
        _store.SaveGame(game);
        if (toWrite.Count == 0)
            return new MarkReport(false, skipped.Count > 0 ? "защищено (ставит только сервер игры): " + string.Join(", ", skipped) : "нечего записывать: всё уже открыто");

        var r = await WriteToSteamAsync(game, toWrite, unlock: true, ct);
        _store.SaveGame(game);
        if (skipped.Count > 0)
            r = r with { Error = (r.Error is null ? "" : r.Error + "; ") + "пропущены защищённые: " + string.Join(", ", skipped) };
        return r;
    }

    // ---------- отметки ----------

    /// <summary>
    /// Ручная отметка "выполнено, но не засчиталось". Что будет со Steam - по Settings.WriteMode.
    /// Снятие отметки в Steam ничего не меняет (для этого есть RevertInSteamAsync).
    /// </summary>
    public async Task<MarkReport> SetManualDoneAsync(GameData game, string apiName, bool done, CancellationToken ct = default)
    {
        var s = game.StateOf(apiName);
        s.ManualDone = done;
        s.ManualAt = done ? DateTimeOffset.Now : null;
        if (!done) game.PendingUnlocks.Remove(apiName);

        // Сохраняем отметку до обращения к Steam: если помощник упадёт, отметка не потеряется.
        _store.SaveGame(game);

        MarkReport report = new(false, null);
        var def = game.Achievements.FirstOrDefault(a => a.ApiName == apiName);
        if (done && !s.SteamUnlocked && def is { Protected: false })
        {
            switch (Settings.WriteMode)
            {
                case WriteMode.MarkAndUnlock:
                    report = await WriteToSteamAsync(game, new[] { apiName }, unlock: true, ct);
                    break;
                case WriteMode.Queue:
                    if (!game.PendingUnlocks.Contains(apiName)) game.PendingUnlocks.Add(apiName);
                    break;
            }
        }
        else if (done && def is { Protected: true } && Settings.WriteMode != WriteMode.MarkOnly)
        {
            report = new MarkReport(false, "достижение защищено: Steam примет его только от сервера игры");
        }
        _store.SaveGame(game);
        return report;
    }

    public void SetNote(GameData game, string apiName, string note)
    {
        game.StateOf(apiName).Note = note;
        _store.SaveGame(game);
    }

    public void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        GameLogic.SetKind(game, apiName, kind);
        _store.SaveGame(game);
    }

    public void SetGroup(GameData game, string apiName, string group)
    {
        game.TagOf(apiName).Group = group.Trim();
        _store.SaveGame(game);
    }

    public void SetManualEstimate(GameData game, string apiName, double? minutes)
    {
        if (minutes is > 0) game.ManualEstimates[apiName] = minutes.Value;
        else game.ManualEstimates.Remove(apiName);
        _store.SaveGame(game);
    }

    public void SetCompletionMinutes(GameData game, double? minutes, string source)
    {
        game.CompletionMinutes = minutes is > 0 ? minutes : null;
        game.CompletionSource = game.CompletionMinutes is null ? "" : source;
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
    }

    public async Task<double?> FetchCompletionAsync(GameData game, CancellationToken ct = default)
    {
        var m = await _completion.GetMedianMinutesAsync(game.AppId, ct);
        if (m is > 0) SetCompletionMinutes(game, m, CompletionTimeSource.Name);
        return m;
    }

    /// <summary>Отправляет накопленную очередь в Steam.</summary>
    public async Task<MarkReport> FlushQueueAsync(GameData game, CancellationToken ct = default)
    {
        var names = game.PendingUnlocks.Where(n => !game.StateOf(n).SteamUnlocked).ToList();
        if (names.Count == 0) return new MarkReport(false, null);
        var r = await WriteToSteamAsync(game, names, unlock: true, ct);
        _store.SaveGame(game);
        return r;
    }

    /// <summary>Снимает в Steam достижение, поставленное через приложение (по журналу).</summary>
    public async Task<MarkReport> RevertInSteamAsync(GameData game, string apiName, CancellationToken ct = default)
    {
        var r = await WriteToSteamAsync(game, new[] { apiName }, unlock: false, ct);
        _store.SaveGame(game);
        return r;
    }

    /// <summary>Было ли достижение выставлено в Steam через приложение (и не снято потом).</summary>
    public bool WasWrittenByApp(uint appId, string apiName)
    {
        var last = _store.ReadLog(appId).LastOrDefault(e => e.ApiName == apiName && e.Ok);
        return last?.Action == "unlock";
    }

    private async Task<MarkReport> WriteToSteamAsync(GameData game, IReadOnlyList<string> names, bool unlock, CancellationToken ct)
    {
        var r = unlock ? await _bridge.UnlockAsync(game.AppId, names, ct) : await _bridge.LockAsync(game.AppId, names, ct);
        var now = DateTimeOffset.Now;
        var action = unlock ? "unlock" : "lock";

        if (r.Changes is null)
        {
            foreach (var n in names)
                _store.AppendLog(new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = n, Action = action, Ok = false, Error = r.Error });
            return new MarkReport(false, r.Error ?? "помощник не вернул результат");
        }

        foreach (var c in r.Changes)
        {
            _store.AppendLog(new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = c.ApiName, Action = action, Ok = c.Ok, Error = c.Error });
            if (!c.Ok) continue;
            var s = game.StateOf(c.ApiName);
            s.SteamUnlocked = unlock;
            s.SteamUnlockTime = unlock ? now : null;
            game.PendingUnlocks.Remove(c.ApiName);
        }
        var failed = r.Changes.Where(c => !c.Ok).ToList();
        return new MarkReport(r.Changes.Any(c => c.Ok),
            failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{f.ApiName}: {f.Error}")));
    }

    // ---------- импорт/экспорт ----------

    public int ImportTags(GameData game, string json)
    {
        var n = TagPack.FromJson(json).ApplyTo(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
        return n;
    }

    // ---------- внутреннее ----------

    private void RequireKey()
    {
        if (!HasApiKey) throw new SteamApiException("Не задан API-ключ (Настройки)");
        if (Settings.SteamId64 == 0) throw new SteamApiException("Не задан SteamID64 (Настройки)");
    }

    /// <summary>
    /// Новая схема из Web API поверх старой: всё, что Web API не отдаёт (описания скрытых, проценты,
    /// проценты по выборке, флаг "защищено"), переносим из уже известного.
    /// </summary>
    internal static void MergeSchema(GameData game, IReadOnlyList<SchemaEntry> entries)
    {
        var old = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        var list = new List<AchievementDef>(entries.Count);
        foreach (var e in entries)
        {
            old.TryGetValue(e.ApiName, out var prev);
            list.Add(new AchievementDef
            {
                ApiName = e.ApiName,
                Name = e.Name,
                // у скрытых Web API описание не отдаёт - оставляем то, что уже знали (из локальной схемы)
                Description = string.IsNullOrEmpty(e.Description) ? prev?.Description ?? "" : e.Description,
                Hidden = e.Hidden,
                IconUrl = e.Icon,
                IconGrayUrl = e.IconGray,
                GlobalPercent = prev?.GlobalPercent,
                SamplePercent = prev?.SamplePercent,
                Protected = prev?.Protected ?? false,
            });
        }
        game.Achievements = list;
    }

    private void MergeLocalSchema(GameData game)
    {
        var local = SteamLocal.ReadSchema(game.AppId, Settings.Language);
        if (local is null || local.Count == 0) return;
        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        foreach (var l in local)
        {
            if (!byName.TryGetValue(l.ApiName, out var def))
            {
                // нет ключа или Web API недоступен - строим список из локальной схемы
                def = new AchievementDef
                {
                    ApiName = l.ApiName,
                    Name = l.Name,
                    Hidden = l.Hidden,
                    IconUrl = AC.Core.SteamFormats.StatsSchemaReader.IconUrl(game.AppId, l.Icon),
                    IconGrayUrl = AC.Core.SteamFormats.StatsSchemaReader.IconUrl(game.AppId, l.IconGray),
                };
                game.Achievements.Add(def);
                byName[l.ApiName] = def;
            }
            if (string.IsNullOrEmpty(def.Description)) def.Description = l.Description;
            def.Protected = l.Protected;
        }
    }
}
