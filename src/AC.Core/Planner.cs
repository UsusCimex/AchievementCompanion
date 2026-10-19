namespace AC.Core;

public enum PlanStage
{
    Missable = 1,
    Story = 2,
    Other = 3,
    Grind = 4,
}

/// <summary>A "can be done in one sitting" group.</summary>
public sealed record PlanGroup(string Label, IReadOnlyList<AchievementRow> Items, double? LongestMinutes);

public sealed record PlanSection(PlanStage Stage, IReadOnlyList<PlanGroup> Groups)
{
    public int Count => Groups.Sum(g => g.Items.Count);
}

/// <param name="Pace">How many times slower than the median the player is (1 - not slower, or unknown).</param>
public sealed record Plan(
    IReadOnlyList<PlanSection> Sections,
    double? RemainingMinutes,
    int UnknownEstimateCount,
    double Pace = 1)
{
    /// <summary>Flat playthrough order.</summary>
    public IEnumerable<AchievementRow> Ordered => Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items);

    /// <summary>Nothing left to unlock.</summary>
    public bool IsComplete => Sections.Count == 0;
}

/// <summary>
/// Order planner.
/// 1) missable, 2) story, 3) other, 4) grind. Within a stage - by time estimate from the start of the game,
/// without an estimate - by global % descending. Groups by the Group label.
/// </summary>
public static class Planner
{
    /// <summary>
    /// How much slower than the median a player may be for us to still trust their pace.
    /// Beyond that it is more likely AFK or hours before achievements existed (fixed on the "Time" tab).
    /// </summary>
    public const double MaxPace = 3;

    public static Plan Build(GameData game, Percentile p, DateTimeOffset? now = null)
    {
        var rows = GameLogic.BuildRows(game, p);
        var open = rows.Where(r => !r.IsDone).ToList();

        var sections = new List<PlanSection>();
        foreach (var stage in new[] { PlanStage.Missable, PlanStage.Story, PlanStage.Other, PlanStage.Grind })
        {
            var items = open.Where(r => StageOf(r.Kind) == stage).ToList();
            if (items.Count == 0) continue;

            var groups = items
                .GroupBy(r => r.Group.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var ordered = g.OrderBy(r => r, RowOrder.Instance).ToList();
                    var longest = ordered.Where(r => r.EstimateMinutes.HasValue).Select(r => r.EstimateMinutes!.Value).DefaultIfEmpty().Max();
                    return new PlanGroup(g.Key, ordered, ordered.Any(r => r.EstimateMinutes.HasValue) ? longest : null);
                })
                // groups are ordered by their earliest item
                .OrderBy(g => g.Items[0], RowOrder.Instance)
                .ToList();

            sections.Add(new PlanSection(stage, groups));
        }

        double pace = PaceOf(game, now ?? DateTimeOffset.Now);
        int unknown = open.Count(r => r.EstimateMinutes is null);
        return new Plan(sections, Remaining(rows, pace), unknown, pace);
    }

    /// <summary>
    /// How many times slower than the median the player is, measured on the median estimates by own unlocks
    /// (<see cref="Progress.MinutesPerUnit"/>), so idle playtime does not count. Needs progress worth a tenth of
    /// the game or half an hour. Faster than the median gives no speed-up (a margin does not hurt),
    /// slower is capped at MaxPace.
    /// </summary>
    public static double PaceOf(GameData game, DateTimeOffset now)
    {
        var typical = game.Estimates.Values.Select(e => e.P50 ?? 0).DefaultIfEmpty().Max();
        var own = Progress.MinutesPerUnit(game, name => game.Estimates.GetValueOrDefault(name)?.P50, now,
            minProgress: Math.Max(30, typical / 10));
        return own is { } o ? Math.Clamp(o, 1, MaxPace) : 1;
    }

    public static PlanStage StageOf(AchievementKind kind) => kind switch
    {
        AchievementKind.Missable => PlanStage.Missable,
        AchievementKind.Story => PlanStage.Story,
        AchievementKind.Grind => PlanStage.Grind,
        _ => PlanStage.Other,
    };

    /// <summary>
    /// Estimates count from the start of the game, so what is left is the way from the player's position
    /// (<see cref="Progress.Position"/>) to the latest locked achievement, with everything before it unlocked along
    /// the way, plus the ones left behind: each costs the stretch from the previous achievement to it.
    /// Computed on the median scale (own estimates are divided by pace), then multiplied by pace.
    /// null - no locked achievement has an estimate.
    /// </summary>
    private static double? Remaining(IReadOnlyList<AchievementRow> rows, double pace)
    {
        var timeline = rows
            .Where(r => r.EstimateMinutes.HasValue)
            .Select(r => (At: r.EstimateIsManual ? r.EstimateMinutes!.Value / pace : r.EstimateMinutes!.Value, Done: r.IsDone))
            .OrderBy(x => x.At)
            .ToList();
        if (timeline.All(x => x.Done)) return null;

        double position = Progress.Position(timeline);
        double ahead = Math.Max(0, timeline.Where(x => !x.Done).Max(x => x.At) - position);
        double behind = 0;
        for (int i = 0; i < timeline.Count; i++)
            if (!timeline[i].Done && timeline[i].At <= position)
                behind += timeline[i].At - (i > 0 ? timeline[i - 1].At : 0);
        return (ahead + behind) * pace;
    }

    private sealed class RowOrder : IComparer<AchievementRow>
    {
        public static readonly RowOrder Instance = new();

        public int Compare(AchievementRow? x, AchievementRow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            // estimated before unestimated
            if (x.EstimateMinutes.HasValue != y.EstimateMinutes.HasValue)
                return x.EstimateMinutes.HasValue ? -1 : 1;
            if (x.EstimateMinutes.HasValue)
            {
                int c = x.EstimateMinutes!.Value.CompareTo(y.EstimateMinutes!.Value);
                if (c != 0) return c;
            }
            // common before rare
            int pc = (y.Def.Percent ?? -1).CompareTo(x.Def.Percent ?? -1);
            if (pc != 0) return pc;
            return string.CompareOrdinal(x.ApiName, y.ApiName);
        }
    }
}
