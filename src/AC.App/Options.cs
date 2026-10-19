using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>Пункт выпадающего списка: значение + подпись.</summary>
public sealed record Option<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

public static class Options
{
    public static IReadOnlyList<Option<AchievementKind>> Kinds { get; } = new[]
    {
        new Option<AchievementKind>(AchievementKind.Unknown, "-"),
        new Option<AchievementKind>(AchievementKind.Story, "Сюжет"),
        new Option<AchievementKind>(AchievementKind.Missable, "Миссибл"),
        new Option<AchievementKind>(AchievementKind.Grind, "Гринд"),
        new Option<AchievementKind>(AchievementKind.Other, "Прочее"),
    };

    /// <summary>Фильтр по типу: null - все.</summary>
    public static IReadOnlyList<Option<AchievementKind?>> KindFilters { get; } = new[]
    {
        new Option<AchievementKind?>(null, "Все типы"),
        new Option<AchievementKind?>(AchievementKind.Story, "Сюжет"),
        new Option<AchievementKind?>(AchievementKind.Missable, "Миссибл"),
        new Option<AchievementKind?>(AchievementKind.Grind, "Гринд"),
        new Option<AchievementKind?>(AchievementKind.Other, "Прочее"),
        new Option<AchievementKind?>(AchievementKind.Unknown, "Без типа"),
    };

    public static IReadOnlyList<Option<SortMode>> Sorts { get; } = new[]
    {
        new Option<SortMode>(SortMode.Plan, "По плану"),
        new Option<SortMode>(SortMode.Time, "По времени"),
        new Option<SortMode>(SortMode.Rarity, "По редкости"),
        new Option<SortMode>(SortMode.Name, "По названию"),
        new Option<SortMode>(SortMode.UnlockTime, "По дате получения"),
    };

    public static IReadOnlyList<Option<Percentile>> Percentiles { get; } = new[]
    {
        new Option<Percentile>(Percentile.P50, "Оценка: медиана"),
        new Option<Percentile>(Percentile.P75, "Оценка: p75"),
        new Option<Percentile>(Percentile.P90, "Оценка: p90"),
    };

    public static IReadOnlyList<Option<AppTheme>> Themes { get; } = new[]
    {
        new Option<AppTheme>(AppTheme.System, "Как в Windows"),
        new Option<AppTheme>(AppTheme.Light, "Светлая"),
        new Option<AppTheme>(AppTheme.Dark, "Тёмная"),
    };

    public static IReadOnlyList<Option<WriteMode>> WriteModes { get; } = new[]
    {
        new Option<WriteMode>(WriteMode.MarkOnly, "Только отмечать (Steam не трогать)"),
        new Option<WriteMode>(WriteMode.MarkAndUnlock, "Отметка + сразу записать в Steam"),
        new Option<WriteMode>(WriteMode.Queue, "Копить и отправлять в Steam по кнопке"),
    };

    public static IReadOnlyList<Option<StatusSource>> StatusSources { get; } = new[]
    {
        new Option<StatusSource>(StatusSource.WebApi, "Steam Web API (нужны ключ и открытые сведения об играх)"),
        new Option<StatusSource>(StatusSource.SteamClient, "Steam-клиент через помощник (работает при скрытом профиле)"),
    };

    public static IReadOnlyList<Option<string>> Languages { get; } = new[]
    {
        new Option<string>("russian", "Русский"),
        new Option<string>("english", "English"),
        new Option<string>("ukrainian", "Українська"),
        new Option<string>("german", "Deutsch"),
        new Option<string>("schinese", "简体中文"),
    };

    public static string KindTitle(AchievementKind k) => Kinds.FirstOrDefault(o => o.Value == k)?.Title ?? "-";

    public static string StageTitle(PlanStage s) => s switch
    {
        PlanStage.Missable => "1. Миссибл и привязанные к выбору",
        PlanStage.Story => "2. Сюжет",
        PlanStage.Other => "3. Прочее",
        PlanStage.Grind => "4. Гринд",
        _ => s.ToString(),
    };
}
