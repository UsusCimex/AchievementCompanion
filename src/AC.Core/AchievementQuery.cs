namespace AC.Core;

public enum SortMode
{
    Plan = 0,
    Time = 1,
    Rarity = 2,
    Name = 3,
    UnlockTime = 4,
}

/// <summary>Фильтр по статусу (чипы над списком).</summary>
public enum StatusFilter
{
    All = 0,
    /// <summary>Не получены: ни в Steam, ни ручной отметкой.</summary>
    Open = 1,
    /// <summary>Получены: открыты в Steam или отмечены вручную.</summary>
    Done = 2,
    /// <summary>Сделано, но в Steam не засчитано.</summary>
    NotCounted = 3,
}

/// <summary>Фильтры и сортировка списка (раздел 3 ТЗ).</summary>
public sealed class AchievementQuery
{
    public SortMode Sort { get; set; } = SortMode.Plan;
    public bool Descending { get; set; }
    public StatusFilter Status { get; set; }
    public bool OnlyHidden { get; set; }
    /// <summary>Пусто - все типы.</summary>
    public HashSet<AchievementKind> Kinds { get; } = new();
    public string Search { get; set; } = "";

    public IReadOnlyList<AchievementRow> Apply(GameData game, Percentile p)
    {
        IEnumerable<AchievementRow> rows = GameLogic.BuildRows(game, p);

        rows = Status switch
        {
            StatusFilter.Open => rows.Where(r => !r.IsDone),
            StatusFilter.Done => rows.Where(r => r.IsDone),
            StatusFilter.NotCounted => rows.Where(r => r.Status == EffectiveStatus.ManualDone),
            _ => rows,
        };
        if (OnlyHidden) rows = rows.Where(r => r.Def.Hidden);
        if (Kinds.Count > 0) rows = rows.Where(r => Kinds.Contains(r.Kind));
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            rows = rows.Where(r =>
                r.Def.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                r.Def.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                r.Def.ApiName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                r.Note.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = rows.ToList();
        switch (Sort)
        {
            case SortMode.Plan:
            {
                var order = Planner.Build(game, p).Ordered
                    .Select((r, i) => (r.ApiName, i))
                    .ToDictionary(x => x.ApiName, x => x.i);
                // открытые (не в плане) уходят в конец
                list = list.OrderBy(r => order.TryGetValue(r.ApiName, out var i) ? i : int.MaxValue)
                           .ThenBy(r => r.Def.Name, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();
                break;
            }
            case SortMode.Time:
                list = list.OrderBy(r => r.EstimateMinutes is null)
                           .ThenBy(r => r.EstimateMinutes)
                           .ThenByDescending(r => r.Def.Percent ?? -1)
                           .ToList();
                break;
            case SortMode.Rarity:
                list = list.OrderBy(r => r.Def.Percent is null)
                           .ThenBy(r => r.Def.Percent)
                           .ToList();
                break;
            case SortMode.Name:
                list = list.OrderBy(r => r.Def.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                break;
            case SortMode.UnlockTime:
                list = list.OrderBy(r => r.UnlockTime is null)
                           .ThenByDescending(r => r.UnlockTime)
                           .ToList();
                break;
        }

        if (Descending) list.Reverse();
        return list;
    }
}
