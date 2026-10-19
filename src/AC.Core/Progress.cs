namespace AC.Core;

/// <summary>
/// How far the player has come and how fast they go, judged by what they unlocked rather than by raw playtime,
/// which also counts AFK, idling and a game left open. On a scale such as "minutes from the start" every achievement
/// owns the stretch from the previous one to it: the stretches of unlocked ones are the progress made, the stretches
/// of locked ones are what is left, whatever the order of unlocks.
/// </summary>
public static class Progress
{
    /// <summary>The stretch each achievement owns; achievements at the same point share it.</summary>
    public static Dictionary<string, double> Stretches(IEnumerable<(string Name, double At)> achievements)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        double previous = 0;
        foreach (var point in achievements.GroupBy(a => a.At).OrderBy(g => g.Key))
        {
            var names = point.Select(a => a.Name).ToList();
            double share = Math.Max(0, point.Key - previous) / names.Count;
            foreach (var name in names) result[name] = share;
            previous = Math.Max(previous, point.Key);
        }
        return result;
    }

    /// <summary>
    /// Own minutes of play per unit of the scale; on the "minutes from the start" scale of a typical player,
    /// 2 means twice as slow. Playtime between two known moments is matched with the progress made between them,
    /// and the result is the median weighted by progress. Stretches with playtime but no unlock (AFK, idling)
    /// are left out entirely. null - less progress than <paramref name="minProgress"/> to measure by.
    /// </summary>
    public static double? MinutesPerUnit(GameData game, Func<string, double?> scale, DateTimeOffset now, double minProgress)
    {
        var stretches = Stretches(game.Achievements
            .Select(a => (a.ApiName, At: scale(a.ApiName)))
            .Where(a => a.At.HasValue)
            .Select(a => (a.ApiName, a.At!.Value)));
        var done = new List<(DateTimeOffset At, double Stretch)>();
        foreach (var (name, stretch) in stretches)
        {
            var state = game.States.GetValueOrDefault(name);
            DateTimeOffset? at = GameLogic.StatusOf(state, game.CountManualMarks) switch
            {
                EffectiveStatus.Unlocked => state!.SteamUnlockTime ?? DateTimeOffset.MinValue,
                EffectiveStatus.ManualDone => state!.ManualAt ?? DateTimeOffset.MinValue,
                _ => null,
            };
            if (at is { } t) done.Add((t, stretch));
        }

        var segments = new List<(double Pace, double Weight)>();
        double lastMinutes = 0, lastProgress = 0;
        foreach (var point in Points(game, now))
        {
            double progress = done.Where(d => d.At <= point.At).Sum(d => d.Stretch);
            double played = point.Minutes - lastMinutes, progressed = progress - lastProgress;
            if (progressed <= 0)
            {
                lastMinutes = point.Minutes;
                continue;
            }
            // Progress seen before its playtime: it goes to the next stretch.
            if (played <= 0) continue;
            segments.Add((played / progressed, progressed));
            lastMinutes = point.Minutes;
            lastProgress = progress;
        }
        return segments.Sum(s => s.Weight) >= minProgress ? WeightedMedian(segments) : null;
    }

    /// <summary>Points kept per game: years of play between sessions.</summary>
    public const int MaxPlaytimePoints = 300;

    /// <summary>
    /// Adds a playtime point to the history, oldest first. A point that contradicts its neighbours is skipped
    /// (Steam playtime only grows); of a stretch with the same playtime only its first and last points stay.
    /// </summary>
    public static void AddPlaytime(GameData game, DateTimeOffset at, double minutes)
    {
        var history = game.PlaytimeHistory;
        int i = history.FindIndex(p => p.At > at);
        if (i < 0) i = history.Count;
        if (i > 0 && history[i - 1].Minutes > minutes) return;
        if (i < history.Count && history[i].Minutes < minutes) return;
        history.Insert(i, new PlaytimePoint { At = at, Minutes = minutes });
        for (int k = history.Count - 2; k >= 1; k--)
            if (history[k - 1].Minutes == history[k].Minutes && history[k + 1].Minutes == history[k].Minutes)
                history.RemoveAt(k);
        if (history.Count > MaxPlaytimePoints) history.RemoveRange(0, history.Count - MaxPlaytimePoints);
    }

    /// <summary>Known playtime moments, oldest first, ending with the current playtime; manual corrections applied.</summary>
    private static IEnumerable<PlaytimePoint> Points(GameData game, DateTimeOffset now)
    {
        if (game.PlaytimeOverrideMinutes is { } own)
        {
            yield return new PlaytimePoint { At = now, Minutes = own };
            yield break;
        }
        var current = new PlaytimePoint { At = now, Minutes = game.PlaytimeMinutes };
        double seen = 0;
        foreach (var p in game.PlaytimeHistory.Where(p => p.At < now).OrderBy(p => p.At).Append(current))
        {
            // Steam playtime only grows; the hours to ignore are taken from the oldest ones.
            seen = Math.Max(seen, p.Minutes);
            yield return new PlaytimePoint { At = p.At, Minutes = Math.Max(0, seen - game.PlaytimeOffsetMinutes) };
        }
    }

    private static double? WeightedMedian(List<(double Value, double Weight)> items)
    {
        if (items.Count == 0) return null;
        items.Sort((a, b) => a.Value.CompareTo(b.Value));
        double half = items.Sum(i => i.Weight) / 2, sum = 0;
        foreach (var (value, weight) in items)
        {
            sum += weight;
            if (sum >= half) return value;
        }
        return items[^1].Value;
    }
}
