using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>Пункт выпадающего списка: значение и подпись по ключу перевода.</summary>
public sealed class Option<T>
{
    private readonly string? _key;
    private readonly string _title;

    public Option(T value, string key) : this(value, key, "") { }

    private Option(T value, string? key, string title)
    {
        Value = value;
        _key = key;
        _title = title;
    }

    /// <summary>Подпись без перевода: названия языков пишутся на самих языках.</summary>
    public static Option<T> Fixed(T value, string title) => new(value, null, title);

    public T Value { get; }
    public string Title => _key is null ? _title : Loc.T(_key);
    public override string ToString() => Title;
}

public static class Options
{
    public static IReadOnlyList<Option<AchievementKind>> Kinds { get; } = new[]
    {
        Option<AchievementKind>.Fixed(AchievementKind.Unknown, "-"),
        new Option<AchievementKind>(AchievementKind.Story, "kind.story"),
        new Option<AchievementKind>(AchievementKind.Missable, "kind.missable"),
        new Option<AchievementKind>(AchievementKind.Grind, "kind.grind"),
        new Option<AchievementKind>(AchievementKind.Other, "kind.other"),
    };

    /// <summary>Фильтр по типу: null - все.</summary>
    public static IReadOnlyList<Option<AchievementKind?>> KindFilters { get; } = new[]
    {
        new Option<AchievementKind?>(null, "kind.all"),
        new Option<AchievementKind?>(AchievementKind.Story, "kind.story"),
        new Option<AchievementKind?>(AchievementKind.Missable, "kind.missable"),
        new Option<AchievementKind?>(AchievementKind.Grind, "kind.grind"),
        new Option<AchievementKind?>(AchievementKind.Other, "kind.other"),
        new Option<AchievementKind?>(AchievementKind.Unknown, "kind.none"),
    };

    public static IReadOnlyList<Option<SortMode>> Sorts { get; } = new[]
    {
        new Option<SortMode>(SortMode.Plan, "sort.plan"),
        new Option<SortMode>(SortMode.Time, "sort.time"),
        new Option<SortMode>(SortMode.Rarity, "sort.rarity"),
        new Option<SortMode>(SortMode.Name, "sort.name"),
        new Option<SortMode>(SortMode.UnlockTime, "sort.unlockTime"),
    };

    public static IReadOnlyList<Option<Percentile>> Percentiles { get; } = new[]
    {
        new Option<Percentile>(Percentile.P50, "percentile.p50"),
        new Option<Percentile>(Percentile.P75, "percentile.p75"),
        new Option<Percentile>(Percentile.P90, "percentile.p90"),
    };

    public static IReadOnlyList<Option<AppTheme>> Themes { get; } = new[]
    {
        new Option<AppTheme>(AppTheme.System, "theme.system"),
        new Option<AppTheme>(AppTheme.Light, "theme.light"),
        new Option<AppTheme>(AppTheme.Dark, "theme.dark"),
    };

    public static IReadOnlyList<Option<WriteMode>> WriteModes { get; } = new[]
    {
        new Option<WriteMode>(WriteMode.MarkOnly, "writeMode.markOnly"),
        new Option<WriteMode>(WriteMode.MarkAndUnlock, "writeMode.markAndUnlock"),
        new Option<WriteMode>(WriteMode.Queue, "writeMode.queue"),
    };

    public static IReadOnlyList<Option<StatusSource>> StatusSources { get; } = new[]
    {
        new Option<StatusSource>(StatusSource.WebApi, "source.webApi"),
        new Option<StatusSource>(StatusSource.SteamClient, "source.client"),
    };

    public static IReadOnlyList<Option<string>> Languages { get; } = new[]
    {
        Option<string>.Fixed("russian", "Русский"),
        Option<string>.Fixed("english", "English"),
        Option<string>.Fixed("ukrainian", "Українська"),
        Option<string>.Fixed("german", "Deutsch"),
        Option<string>.Fixed("schinese", "简体中文"),
    };

    public static string KindTitle(AchievementKind k) => Kinds.FirstOrDefault(o => o.Value == k)?.Title ?? "-";

    public static string StageTitle(PlanStage s) => s switch
    {
        PlanStage.Missable => Loc.T("stage.missable"),
        PlanStage.Story => Loc.T("stage.story"),
        PlanStage.Other => Loc.T("stage.other"),
        PlanStage.Grind => Loc.T("stage.grind"),
        _ => s.ToString(),
    };
}
