namespace AC.Core;

/// <summary>Combines statuses and builds the list rows.</summary>
public static class GameLogic
{
    public static EffectiveStatus StatusOf(AchievementState? s, bool manualMarks = true)
    {
        if (s is null) return EffectiveStatus.Locked;
        if (s.SteamUnlocked) return EffectiveStatus.Unlocked;
        if (manualMarks && s.ManualDone) return EffectiveStatus.ManualDone;
        return EffectiveStatus.Locked;
    }

    /// <summary>
    /// Applies fresh status from Steam. Manual marks and notes are kept:
    /// once the achievement unlocks in Steam, its status becomes "unlocked" by itself.
    /// Returns the api names unlocked since the last sync.
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
            var status = StatusOf(s, game.CountManualMarks);
            rows.Add(new AchievementRow(
                def,
                status,
                t?.Kind ?? AchievementKind.Unknown,
                t?.Group ?? "",
                est,
                manual,
                s?.Note ?? "",
                status switch
                {
                    EffectiveStatus.Unlocked => s!.SteamUnlockTime,
                    EffectiveStatus.ManualDone => s!.ManualAt,
                    _ => null,
                },
                t is { Group.Length: > 0, GroupIsManual: false }));
        }
        return rows;
    }

    /// <summary>Progress: (unlocked in Steam, marked manually, total).</summary>
    public static (int Unlocked, int Manual, int Total) Progress(GameData game)
    {
        int u = 0, m = 0;
        foreach (var def in game.Achievements)
        {
            switch (StatusOf(game.States.GetValueOrDefault(def.ApiName), game.CountManualMarks))
            {
                case EffectiveStatus.Unlocked: u++; break;
                case EffectiveStatus.ManualDone: m++; break;
            }
        }
        return (u, m, game.Achievements.Count);
    }

    /// <summary>
    /// Sets the automatic kind and group ("in chapter 3" - group "Chapter 3") wherever they are not set manually.
    /// </summary>
    public static void AutoClassify(GameData game)
    {
        foreach (var def in game.Achievements)
        {
            var tag = game.TagOf(def.ApiName);
            if (tag.Source != KindSource.Manual)
            {
                tag.Kind = KindClassifier.Classify(def.Name, def.Description);
                tag.Source = KindSource.Auto;
            }
            if (!tag.GroupIsManual)
            {
                tag.Group = KindClassifier.Location(def.Name, def.Description) ?? "";
                tag.GroupSource = KindSource.Auto;
            }
        }
    }

    public static void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        var tag = game.TagOf(apiName);
        tag.Kind = kind;
        tag.Source = KindSource.Manual;
    }

    /// <summary>A user-set group. An empty one is manual too: auto-detection will not fill it again.</summary>
    public static void SetGroup(GameData game, string apiName, string group)
    {
        var tag = game.TagOf(apiName);
        tag.Group = group.Trim();
        tag.GroupSource = KindSource.Manual;
    }
}
