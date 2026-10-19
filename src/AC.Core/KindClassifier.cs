using System.Text.RegularExpressions;

namespace AC.Core;

/// <summary>
/// Грубая эвристика типа по названию и описанию (английский и русский).
/// Задача - дать разумный старт, дальше пользователь правит руками.
/// Порядок проверок важен: миссибл сильнее сюжета, гринд сильнее "прочего".
/// Русские глаголы - во всех частых формах: Steam пишет и "Соберите", и "Собрать", и "Собрал".
/// </summary>
public static class KindClassifier
{
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Missable = new(
        @"\bmissable\b|point of no return|before (leaving|you leave|the end|finishing|completing)|" +
        @"without (ever )?(dying|being (seen|detected|hit))|in a single (run|playthrough|life)|" +
        @"\bspare\b|\bsave (him|her|them)\b|" +
        @"пропуст|безвозврат|до того как|прежде чем|ни разу не|не умира|не погиб|за одно прохождение|пощад",
        Opt);

    // Число после "главы", "акта", "эпизода" - номер места, а не счётчик гринда.
    private static readonly Regex Grind = new(
        @"\b(collect|find|kill|defeat|destroy|earn|gather|craft|win|complete|reach|get|open|catch)\b[^.]{0,40}" +
        @"\b((?<!\b(chapter|act|episode)\s+)\d{2,}[\d,\s]*|all|every)\b|" +
        @"\b\d{3,}\b|\ball (the )?(collectibles|items|weapons|cards|trophies|achievements|secrets|upgrades)\b|" +
        @"\bmax(imum|ed)? level\b|\blevel \d{2,}\b|" +
        @"(собер|собра|найд|найти|наш[её]л|нашли|уби|убе|уничтож|заработ|победи|выигра|откр|дости[гч]|получ|выполн|накоп)[^.]{0,40}" +
        @"((?<!\b(глав|акт|эпизод)[а-я]*\s+)\b\d{2,}|\bвс[её]х?\b|\bкажд)|" +
        @"максимальн[а-я]* уров|уровн[яе] \d{2,}",
        Opt);

    // Только "пройти/завершить что-то сюжетное" и слова, сюжетные сами по себе.
    // Просто упоминание главы ("сердце в главе 3") - это место: оно уходит в группу, а не в тип.
    private static readonly Regex Story = new(
        @"\b(complete|finish|beat|clear)\b[^.]{0,30}\b(chapter|act|prologue|epilogue|mission|the game|the story|campaign|level \d|episode|part)\b|" +
        @"\bstory\b|\bending\b|\bfinal boss\b|\bprologue\b|\bepilogue\b|" +
        @"(пройд|пройти|прош[её]л|прошли|заверш|законч)[^.]{0,30}(глав|акт|пролог|эпилог|мисси|игру|сюжет|кампани|эпизод)|" +
        @"пролог|эпилог|концовк|финальн[а-я]* босс|\bсюжет",
        Opt);

    // "в главе 3", "Chapter IV", "акта 2" - для автоматической группы "за один заход".
    private static readonly Regex Place = new(
        @"\b(?<kind>chapter|act|episode)\s+(?<n>\d{1,3}|[ivx]{1,5})\b|" +
        @"\b(?<kind>глав(а|ы|е|у|ой|ою)|акт(а|е|у|ом)?|эпизод(а|е|у|ом)?)\s+(?<n>\d{1,3}|[ivx]{1,5})\b",
        Opt);

    public static AchievementKind Classify(string? name, string? description)
    {
        var text = $"{name} {description}".Trim();
        if (text.Length == 0) return AchievementKind.Unknown;
        if (Missable.IsMatch(text)) return AchievementKind.Missable;
        if (Story.IsMatch(text) && !Grind.IsMatch(text)) return AchievementKind.Story;
        if (Grind.IsMatch(text)) return AchievementKind.Grind;
        if (Story.IsMatch(text)) return AchievementKind.Story;
        return AchievementKind.Other;
    }

    /// <summary>
    /// Где получают достижение: "Глава 3", "Акт 2", "Эпизод 1" или null.
    /// Описание смотрим раньше названия: оно точнее.
    /// </summary>
    public static string? Location(string? name, string? description)
    {
        foreach (var text in new[] { description, name })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var m = Place.Match(text);
            if (!m.Success) continue;
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var label = kind.StartsWith("ch") || kind.StartsWith("глав") ? "Глава"
                : kind.StartsWith("ep") || kind.StartsWith("эпизод") ? "Эпизод"
                : "Акт";
            return $"{label} {m.Groups["n"].Value.ToUpperInvariant()}";
        }
        return null;
    }
}
