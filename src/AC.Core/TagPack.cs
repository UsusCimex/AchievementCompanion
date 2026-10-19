using System.Text.Json;

namespace AC.Core;

/// <summary>
/// Файл разметки игры: типы, группы, ручные оценки. Его можно собрать по гайду,
/// сохранить и перенести на другой компьютер или отдать другу.
/// </summary>
public sealed class TagPack
{
    public int Version { get; set; } = 1;
    public uint AppId { get; set; }
    public string Game { get; set; } = "";
    public double? CompletionMinutes { get; set; }
    public List<TagPackItem> Items { get; set; } = new();

    public static TagPack From(GameData game)
    {
        var pack = new TagPack { AppId = game.AppId, Game = game.Name, CompletionMinutes = game.CompletionMinutes };
        foreach (var def in game.Achievements)
        {
            game.Tags.TryGetValue(def.ApiName, out var t);
            bool manualKind = t?.Source == KindSource.Manual;
            // автоматические группы ("в главе 3") у получателя определятся сами
            bool hasGroup = t is { GroupIsManual: true } && !string.IsNullOrWhiteSpace(t.Group);
            bool hasEst = game.ManualEstimates.TryGetValue(def.ApiName, out var est);
            if (!manualKind && !hasGroup && !hasEst) continue;
            pack.Items.Add(new TagPackItem
            {
                ApiName = def.ApiName,
                Kind = manualKind ? t!.Kind : null,
                Group = hasGroup ? t!.Group : null,
                EstimateMinutes = hasEst ? est : null,
            });
        }
        return pack;
    }

    /// <summary>Применяет разметку. Возвращает число применённых записей (неизвестные api-имена пропускаются).</summary>
    public int ApplyTo(GameData game)
    {
        var known = game.Achievements.Select(a => a.ApiName).ToHashSet(StringComparer.Ordinal);
        int applied = 0;
        foreach (var item in Items)
        {
            if (!known.Contains(item.ApiName)) continue;
            var tag = game.TagOf(item.ApiName);
            if (item.Kind.HasValue)
            {
                tag.Kind = item.Kind.Value;
                tag.Source = KindSource.Manual;
            }
            if (item.Group is not null)
            {
                tag.Group = item.Group;
                tag.GroupSource = KindSource.Manual;
            }
            if (item.EstimateMinutes.HasValue) game.ManualEstimates[item.ApiName] = item.EstimateMinutes.Value;
            applied++;
        }
        if (CompletionMinutes is > 0 && game.CompletionMinutes is null) game.CompletionMinutes = CompletionMinutes;
        return applied;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Pretty);

    public static TagPack FromJson(string json) =>
        JsonSerializer.Deserialize<TagPack>(json, JsonOptions.Pretty) ?? throw new FormatException("Пустой файл разметки");
}

public sealed class TagPackItem
{
    public string ApiName { get; set; } = "";
    public AchievementKind? Kind { get; set; }
    public string? Group { get; set; }
    public double? EstimateMinutes { get; set; }
}
