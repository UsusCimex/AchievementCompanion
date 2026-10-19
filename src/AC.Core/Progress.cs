namespace AC.Core;

/// <summary>
/// Where the player is in a game and how fast they go, judged by what they unlocked rather than by raw playtime,
/// which also counts AFK, idling and a game left open.
/// </summary>
public static class Progress
{
    /// <summary>
    /// Position on a scale such as "minutes from the start": the furthest unlocked achievement, 0 - none yet.
    /// A lucky rare one puts it too far, but then the locked ones behind it still cost their stretches
    /// (see Planner), and a pace measured too fast is not used (no speed-up).
    /// </summary>
    public static double Position(IEnumerable<(double At, bool Done)> achievements) =>
        achievements.Where(a => a.Done).Select(a => a.At).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Own minutes of play per unit of the scale; on the "minutes from the start" scale of a typical player,
    /// 2 means twice as slow. Playtime between two known moments is matched with the progress made between them,
    /// and the result is the median weighted by progress. Stretches with playtime but no progress (AFK, idling)
    /// are left out entirely. null - less progress than <paramref name="minProgress"/> to measure by.
    /// </summary>
    public static double? MinutesPerUnit(GameData game, Func<string, double?> scale, DateTimeOffset now, double minProgress)
    {
        var timeline = new List<(double At, DateTimeOffset DoneAt)>();
        foreach (var a in game.Achievements)
        {
            if (scale(a.ApiName) is not { } at) continue;
            var state = game.States.GetValueOrDefault(a.ApiName);
            var doneAt = GameLogic.StatusOf(state, game.CountManualMarks) switch
            {
                EffectiveStatus.Unlocked => state!.SteamUnlockTime ?? DateTimeOffset.MinValue,
                EffectiveStatus.ManualDone => state!.ManualAt ?? DateTimeOffset.MinValue,
                _ => DateTimeOffset.MaxValue,
            };
            timeline.Add((at, doneAt));
        }

        var segments = new List<(double Pace, double Weight)>();
        double lastMinutes = 0, lastPosition = 0;
        foreach (var point in Points(game, now))
        {
            var position = Position(timeline.Select(t => (t.At, t.DoneAt <= point.At)));
            double played = point.Minutes - lastMinutes, progressed = position - lastPosition;
            if (progressed <= 0)
            {
                lastMinutes = point.Minutes;
                continue;
            }
            // Progress seen before its playtime: it goes to the next stretch.
            if (played <= 0) continue;
            segments.Add((played / progressed, progressed));
            lastMinutes = point.Minutes;
            lastPosition = position;
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
