namespace AC.Core;

/// <summary>Сведение статусов и сборка строк для списка.</summary>
public static class GameLogic
{
    public static EffectiveStatus StatusOf(AchievementState? s)
    {
        if (s is null) return EffectiveStatus.Locked;
        if (s.SteamUnlocked) return EffectiveStatus.Unlocked;
        if (s.ManualDone) return EffectiveStatus.ManualDone;
        return EffectiveStatus.Locked;
    }

    /// <summary>
    /// Применяет свежий статус из Steam. Ручная отметка и заметка не стираются:
    /// если достижение потом открылось в Steam, статус сам станет "открыто".
    /// Возвращает api-имена, которые открылись с прошлой синхронизации.
    /// </summary>
    public static IReadOnlyList<string> ApplySteamState(
        GameData game,
        IEnumerable<(string ApiName, bool Unlocked, DateTimeOffset? UnlockTime)> steam,
        DateTimeOffset now)
    {
        var newlyUnlocked = new List<string>();
        foreach (var (apiName, unlocked, time) in steam)
        {
            var s = game.StateOf(apiName);
            if (unlocked && !s.SteamUnlocked) newlyUnlocked.Add(apiName);
            s.SteamUnlocked = unlocked;
            s.SteamUnlockTime = unlocked ? time : null;
        }
        game.StateFetchedAt = now;
        return newlyUnlocked;
    }

    public static double? EstimateOf(GameData game, string apiName, Percentile p, out bool manual)
    {
        if (game.ManualEstimates.TryGetValue(apiName, out var m))
        {
            manual = true;
            return m;
        }
        manual = false;
        return game.Estimates.TryGetValue(apiName, out var e) ? e.Get(p) : null;
    }

    public static IReadOnlyList<AchievementRow> BuildRows(GameData game, Percentile p)
    {
        var rows = new List<AchievementRow>(game.Achievements.Count);
        foreach (var def in game.Achievements)
        {
            game.States.TryGetValue(def.ApiName, out var s);
            game.Tags.TryGetValue(def.ApiName, out var t);
            var est = EstimateOf(game, def.ApiName, p, out var manual);
            rows.Add(new AchievementRow(
                def,
                StatusOf(s),
                t?.Kind ?? AchievementKind.Unknown,
                t?.Group ?? "",
                est,
                manual,
                s?.Note ?? "",
                s?.SteamUnlocked == true ? s.SteamUnlockTime : s?.ManualAt));
        }
        return rows;
    }

    /// <summary>Прогресс: (открыто в Steam, отмечено вручную, всего).</summary>
    public static (int Unlocked, int Manual, int Total) Progress(GameData game)
    {
        int u = 0, m = 0;
        foreach (var def in game.Achievements)
        {
            switch (StatusOf(game.States.GetValueOrDefault(def.ApiName)))
            {
                case EffectiveStatus.Unlocked: u++; break;
                case EffectiveStatus.ManualDone: m++; break;
            }
        }
        return (u, m, game.Achievements.Count);
    }

    /// <summary>Ставит автоматический тип всем, у кого нет ручной разметки.</summary>
    public static void AutoClassify(GameData game)
    {
        foreach (var def in game.Achievements)
        {
            var tag = game.TagOf(def.ApiName);
            if (tag.Source == KindSource.Manual) continue;
            tag.Kind = KindClassifier.Classify(def.Name, def.Description);
            tag.Source = KindSource.Auto;
        }
    }

    public static void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        var tag = game.TagOf(apiName);
        tag.Kind = kind;
        tag.Source = KindSource.Manual;
    }
}
