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

/// <param name="Pace">Во сколько раз игрок медленнее медианы (1 - не медленнее или не знаем).</param>
/// <param name="Overtime">Наиграно больше, чем обычно уходит на 100%, а открыто не всё: остаток не оценить.</param>
public sealed record Plan(
    IReadOnlyList<PlanSection> Sections,
    double? RemainingMinutes,
    double? CompletionCapMinutes,
    int UnknownEstimateCount,
    double Pace = 1,
    bool Overtime = false)
{
    /// <summary>Плоский порядок прохождения.</summary>
    public IEnumerable<AchievementRow> Ordered => Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items);

    /// <summary>Неоткрытых не осталось.</summary>
    public bool IsComplete => Sections.Count == 0;
}

/// <summary>
/// Планировщик порядка.
/// 1) миссибл, 2) сюжет, 3) прочее, 4) гринд. Внутри этапа - по оценке времени от начала игры,
/// без оценки - по убыванию глобального %. Группы по метке Group.
/// </summary>
public static class Planner
{
    /// <summary>
    /// Насколько игрок может быть медленнее медианы, чтобы мы ещё верили его темпу.
    /// Больше - это скорее AFK или часы до появления достижений (они правятся во вкладке "Время").
    /// </summary>
    public const double MaxPace = 3;

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

        double played = game.EffectivePlaytimeMinutes;
        double? total = game.UsedCompletionMinutes is > 0 ? game.UsedCompletionMinutes : null;
        double pace = PaceOf(game, total);
        int unknown = sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).Count(r => r.EstimateMinutes is null);
        bool overtime = sections.Count > 0 && total is { } t && played >= t * pace;
        double? cap = total is { } t2 ? Math.Max(0, t2 * pace - played) : null;
        double? remaining = overtime ? null : Remaining(sections, played, pace);
        if (remaining.HasValue && cap.HasValue) remaining = Math.Min(remaining.Value, cap.Value);
        return new Plan(sections, remaining, cap, unknown, pace, overtime);
    }

    /// <summary>
    /// Во сколько раз игрок медленнее медианы - по своему прогрессу (TimeEstimator.PersonalCompletion).
    /// Быстрее медианы не ускоряем (запас не вредит), медленнее - не больше MaxPace.
    /// </summary>
    public static double PaceOf(GameData game, double? total)
    {
        if (total is not > 0) return 1;
        var own = TimeEstimator.PersonalCompletion(game);
        return own is { } o && o > total.Value ? Math.Min(o / total.Value, MaxPace) : 1;
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
    /// Считаем на шкале медианы: наигранное и свои оценки делим на темп, а ответ умножаем обратно.
    /// </summary>
    private static double? Remaining(IReadOnlyList<PlanSection> sections, double playedMinutes, double pace)
    {
        bool any = false;
        double mainPath = 0;
        double extra = 0;

        foreach (var s in sections)
        {
            foreach (var g in s.Groups)
            {
                double? longest = null;
                foreach (var r in g.Items)
                {
                    if (r.EstimateMinutes is not { } m) continue;
                    var onScale = r.EstimateIsManual ? m / pace : m;
                    longest = Math.Max(longest ?? 0, onScale);
                }
                if (longest is null) continue;
                any = true;
                if (s.Stage is PlanStage.Missable or PlanStage.Story)
                    mainPath = Math.Max(mainPath, longest.Value);
                else
                    extra += longest.Value;
            }
        }

        if (!any) return null;
        // "Прочее" и "гринд" тоже оценены от начала игры; если игрок уже наиграл больше,
        // чем сюжетный путь, вычитаем наигранное только из сюжетной части.
        return (Math.Max(0, mainPath - playedMinutes / pace) + extra) * pace;
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
