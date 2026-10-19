namespace AC.Core;

public enum PlanStage
{
    Missable = 1,
    Story = 2,
    Other = 3,
    Grind = 4,
}

/// <summary>Группа "можно сделать за один заход".</summary>
public sealed record PlanGroup(string Label, IReadOnlyList<AchievementRow> Items, double? LongestMinutes);

public sealed record PlanSection(PlanStage Stage, IReadOnlyList<PlanGroup> Groups)
{
    public int Count => Groups.Sum(g => g.Items.Count);
}

public sealed record Plan(
    IReadOnlyList<PlanSection> Sections,
    double? RemainingMinutes,
    double? CompletionCapMinutes,
    int UnknownEstimateCount)
{
    /// <summary>Плоский порядок прохождения.</summary>
    public IEnumerable<AchievementRow> Ordered => Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items);
}

/// <summary>
/// Планировщик порядка (раздел 5 ТЗ).
/// 1) миссибл, 2) сюжет, 3) прочее, 4) гринд. Внутри этапа - по оценке времени от начала игры,
/// без оценки - по убыванию глобального %. Группы по метке Group.
/// </summary>
public static class Planner
{
    public static Plan Build(GameData game, Percentile p)
    {
        var open = GameLogic.BuildRows(game, p).Where(r => !r.IsDone).ToList();

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
                // группы упорядочены по самому раннему элементу
                .OrderBy(g => g.Items[0], RowOrder.Instance)
                .ToList();

            sections.Add(new PlanSection(stage, groups));
        }

        double? remaining = Remaining(sections, game.EffectivePlaytimeMinutes, out int unknown);
        double? cap = game.UsedCompletionMinutes is > 0 ? Math.Max(0, game.UsedCompletionMinutes.Value - game.EffectivePlaytimeMinutes) : null;
        if (remaining.HasValue && cap.HasValue) remaining = Math.Min(remaining.Value, cap.Value);
        return new Plan(sections, remaining, cap, unknown);
    }

    public static PlanStage StageOf(AchievementKind kind) => kind switch
    {
        AchievementKind.Missable => PlanStage.Missable,
        AchievementKind.Story => PlanStage.Story,
        AchievementKind.Grind => PlanStage.Grind,
        _ => PlanStage.Other,
    };

    /// <summary>
    /// Остаток = (самое позднее среди неоткрытых сюжетных и миссибл - уже наиграно)
    /// + сумма по группам "прочее" и "гринд" (за группу - самая долгая в ней).
    /// Оценки здесь - "время от начала игры", поэтому сюжетные перекрываются, а не складываются.
    /// </summary>
    private static double? Remaining(IReadOnlyList<PlanSection> sections, double playedMinutes, out int unknown)
    {
        unknown = sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).Count(r => r.EstimateMinutes is null);
        bool any = false;
        double mainPath = 0;
        double extra = 0;

        foreach (var s in sections)
        {
            foreach (var g in s.Groups)
            {
                if (g.LongestMinutes is null) continue;
                any = true;
                if (s.Stage is PlanStage.Missable or PlanStage.Story)
                    mainPath = Math.Max(mainPath, g.LongestMinutes.Value);
                else
                    extra += g.LongestMinutes.Value;
            }
        }

        if (!any) return null;
        // "Прочее" и "гринд" тоже оценены от начала игры; если игрок уже наиграл больше,
        // чем сюжетный путь, вычитаем наигранное только из сюжетной части.
        return Math.Max(0, mainPath - playedMinutes) + extra;
    }

    private sealed class RowOrder : IComparer<AchievementRow>
    {
        public static readonly RowOrder Instance = new();

        public int Compare(AchievementRow? x, AchievementRow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            // с оценкой раньше, чем без
            if (x.EstimateMinutes.HasValue != y.EstimateMinutes.HasValue)
                return x.EstimateMinutes.HasValue ? -1 : 1;
            if (x.EstimateMinutes.HasValue)
            {
                int c = x.EstimateMinutes!.Value.CompareTo(y.EstimateMinutes!.Value);
                if (c != 0) return c;
            }
            // частые раньше редких
            int pc = (y.Def.Percent ?? -1).CompareTo(x.Def.Percent ?? -1);
            if (pc != 0) return pc;
            return string.CompareOrdinal(x.ApiName, y.ApiName);
        }
    }
}
