using System.Text.RegularExpressions;

namespace AC.Core;

/// <summary>
/// Грубая эвристика типа по названию и описанию (английский и русский).
/// Задача - дать разумный старт, дальше пользователь правит руками.
/// Порядок проверок важен: миссибл сильнее сюжета, гринд сильнее "прочего".
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

    private static readonly Regex Grind = new(
        @"\b(collect|find|kill|defeat|destroy|earn|gather|craft|win|complete|reach|get|open|catch)\b[^.]{0,40}\b(\d{2,}[\d,\s]*|all|every)\b|" +
        @"\b\d{3,}\b|\ball (the )?(collectibles|items|weapons|cards|trophies|achievements|secrets|upgrades)\b|" +
        @"\bmax(imum|ed)? level\b|\blevel \d{2,}\b|" +
        @"(собер|найд|убе|уничтож|заработ|победи|выиграй|открой|достигн)[^.]{0,40}(\d{2,}|все|всех|каждо)|" +
        @"\b\d{3,}\b|максимальн[а-я]* уров|уровн[яе] \d{2,}",
        Opt);

    private static readonly Regex Story = new(
        @"\b(complete|finish|beat|clear)\b[^.]{0,30}\b(chapter|act|prologue|epilogue|mission|the game|the story|campaign|level \d|episode|part)\b|" +
        @"\b(chapter|act|episode|prologue|epilogue)\b|\bstory\b|\bending\b|\bfinal boss\b|" +
        @"(пройд|заверш|законч)[^.]{0,30}(глав|акт|пролог|эпилог|мисси|игру|сюжет|кампани|эпизод)|" +
        @"\bглав[аеуы]\b|\bакт\b|пролог|эпилог|концовк|финальн[а-я]* босс",
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
}
