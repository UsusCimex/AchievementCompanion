using System.Text.Json.Serialization;

namespace AC.Core;

/// <summary>Тип достижения. Steam знает только флаг hidden, остальное - эвристика или ручная разметка.</summary>
public enum AchievementKind
{
    Unknown = 0,
    Story = 1,
    Grind = 2,
    Missable = 3,
    Other = 4,
}

public enum KindSource
{
    Auto = 0,
    Manual = 1,
}

/// <summary>Итоговый статус, который видит пользователь.</summary>
public enum EffectiveStatus
{
    Locked = 0,
    Unlocked = 1,
    /// <summary>Выполнено, но в Steam не засчиталось (ручная отметка).</summary>
    ManualDone = 2,
}

/// <summary>Описание достижения из схемы игры.</summary>
public sealed class AchievementDef
{
    public string ApiName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public string IconUrl { get; set; } = "";
    public string IconGrayUrl { get; set; } = "";
    /// <summary>Глобальный процент получения из Steam, 0..100. null - Steam не отдал.</summary>
    public double? GlobalPercent { get; set; }
    /// <summary>Процент по своей выборке игроков (если Steam глобальную статистику не отдаёт).</summary>
    public double? SamplePercent { get; set; }

    /// <summary>Процент, который показываем и по которому считаем: из Steam, иначе из выборки.</summary>
    [JsonIgnore]
    public double? Percent => GlobalPercent ?? SamplePercent;

    [JsonIgnore]
    public bool PercentFromSample => GlobalPercent is null && SamplePercent is not null;
    /// <summary>Защищено: ставится только сервером игры, досчитать через Steam-клиент нельзя.</summary>
    public bool Protected { get; set; }
}

/// <summary>Состояние достижения у игрока.</summary>
public sealed class AchievementState
{
    public bool SteamUnlocked { get; set; }
    public DateTimeOffset? SteamUnlockTime { get; set; }
    public bool ManualDone { get; set; }
    public DateTimeOffset? ManualAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Разметка достижения: тип и группа "за один заход".</summary>
public sealed class AchievementTag
{
    public AchievementKind Kind { get; set; }
    public KindSource Source { get; set; }
    /// <summary>Метка группы: глава, локация, режим. Пусто - без группы.</summary>
    public string Group { get; set; } = "";
}

/// <summary>Оценка времени "от начала игры" в минутах.</summary>
public sealed class TimeEstimate
{
    public string Source { get; set; } = "";
    public double? P50 { get; set; }
    public double? P75 { get; set; }
    public double? P90 { get; set; }
    public int SampleSize { get; set; }

    public double? Get(Percentile p) => p switch
    {
        Percentile.P75 => P75 ?? P50,
        Percentile.P90 => P90 ?? P75 ?? P50,
        _ => P50,
    };
}

public enum Percentile
{
    P50 = 50,
    P75 = 75,
    P90 = 90,
}

/// <summary>Всё, что хранится по одной игре.</summary>
public sealed class GameData
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Наиграно по данным Steam.</summary>
    public int PlaytimeMinutes { get; set; }
    /// <summary>
    /// Своё "реально наиграно" в минутах. Если задано - заменяет данные Steam
    /// (например, много AFK или часть игры пройдена в другой версии).
    /// </summary>
    public double? PlaytimeOverrideMinutes { get; set; }
    /// <summary>
    /// Сколько минут из времени Steam не считать: часы до того, как в игру добавили достижения,
    /// или AFK. Работает, только если PlaytimeOverrideMinutes не задан.
    /// </summary>
    public double PlaytimeOffsetMinutes { get; set; }

    /// <summary>Время, от которого считается прогресс к 100%.</summary>
    [JsonIgnore]
    public double EffectivePlaytimeMinutes =>
        PlaytimeOverrideMinutes ?? Math.Max(0, PlaytimeMinutes - PlaytimeOffsetMinutes);
    /// <summary>Время до 100% по игре в минутах (медиана с внешнего сайта или вручную).</summary>
    public double? CompletionMinutes { get; set; }
    public DateTimeOffset? SchemaFetchedAt { get; set; }
    public DateTimeOffset? StateFetchedAt { get; set; }
    public string SchemaLanguage { get; set; } = "";

    public List<AchievementDef> Achievements { get; set; } = new();
    public Dictionary<string, AchievementState> States { get; set; } = new();
    public Dictionary<string, AchievementTag> Tags { get; set; } = new();
    /// <summary>Автоматические оценки времени.</summary>
    public Dictionary<string, TimeEstimate> Estimates { get; set; } = new();
    /// <summary>Ручные оценки времени (минуты), важнее автоматических.</summary>
    public Dictionary<string, double> ManualEstimates { get; set; } = new();
    /// <summary>Отмечены и ждут отправки в Steam (режим "накопить и отправить").</summary>
    public List<string> PendingUnlocks { get; set; } = new();
    /// <summary>Откуда взято время до 100%: "manual", "completionist.me" и т.п.</summary>
    public string CompletionSource { get; set; } = "";

    /// <summary>
    /// Время до 100%, выведенное из своего прогресса, когда CompletionMinutes не задан.
    /// Считается заново при каждой оценке, не сохраняется.
    /// </summary>
    [JsonIgnore]
    public double? DerivedCompletionMinutes { get; set; }

    /// <summary>Время до 100%, которым реально пользуются оценки: заданное или выведенное.</summary>
    [JsonIgnore]
    public double? UsedCompletionMinutes =>
        CompletionMinutes is > 0 ? CompletionMinutes :
        SampleCompletionMinutes is > 0 ? SampleCompletionMinutes :
        DerivedCompletionMinutes;

    /// <summary>Медиана времени до 100% по выборке игроков.</summary>
    public double? SampleCompletionMinutes { get; set; }
    /// <summary>Сколько игроков в выборке (с открытым профилем).</summary>
    public int SamplePlayers { get; set; }
    /// <summary>Сколько из них прошли на 100%.</summary>
    public int SampleCompleters { get; set; }
    public DateTimeOffset? SampleFetchedAt { get; set; }

    public AchievementState StateOf(string apiName)
    {
        if (!States.TryGetValue(apiName, out var s))
        {
            s = new AchievementState();
            States[apiName] = s;
        }
        return s;
    }

    public AchievementTag TagOf(string apiName)
    {
        if (!Tags.TryGetValue(apiName, out var t))
        {
            t = new AchievementTag();
            Tags[apiName] = t;
        }
        return t;
    }
}

/// <summary>Строка списка: схема + состояние + разметка + оценка.</summary>
public sealed record AchievementRow(
    AchievementDef Def,
    EffectiveStatus Status,
    AchievementKind Kind,
    string Group,
    double? EstimateMinutes,
    bool EstimateIsManual,
    string Note,
    DateTimeOffset? UnlockTime)
{
    public string ApiName => Def.ApiName;
    public bool IsDone => Status != EffectiveStatus.Locked;
}
