namespace AC.Core;

/// <summary>A day with unlocks: what it gave, in order, and how many were unlocked by its end.</summary>
public sealed record UnlockDay(DateOnly Day, IReadOnlyList<AchievementDef> Unlocked, int Total);

/// <summary>Steam unlocks of a game by day: the pace and the breaks.</summary>
public static class UnlockHistory
{
    /// <summary>Days with unlocks in the given time zone, oldest first. Unlocks without a date count in every total.</summary>
    public static IReadOnlyList<UnlockDay> ByDay(GameData game, TimeZoneInfo zone)
    {
        var unlocked = game.Achievements
            .Select(a => (Def: a, State: game.States.GetValueOrDefault(a.ApiName)))
            .Where(x => x.State is { SteamUnlocked: true })
            .ToList();
        int total = unlocked.Count(x => x.State!.SteamUnlockTime is null);
        var days = new List<UnlockDay>();
        foreach (var day in unlocked
                     .Where(x => x.State!.SteamUnlockTime is not null)
                     .OrderBy(x => x.State!.SteamUnlockTime)
                     .GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.State!.SteamUnlockTime!.Value, zone).DateTime)))
        {
            total += day.Count();
            days.Add(new UnlockDay(day.Key, day.Select(x => x.Def).ToList(), total));
        }
        return days;
    }

    /// <summary>The longest stretch without unlocks: between two days with them, or from the last one to today.</summary>
    public static (DateOnly From, DateOnly To)? LongestBreak(IReadOnlyList<UnlockDay> days, DateOnly today)
    {
        (DateOnly From, DateOnly To)? longest = null;
        for (int i = 0; i < days.Count; i++)
        {
            var to = i + 1 < days.Count ? days[i + 1].Day : today;
            if (longest is not { } l || to.DayNumber - days[i].Day.DayNumber >= l.To.DayNumber - l.From.DayNumber)
                longest = (days[i].Day, to);
        }
        return longest;
    }
}
