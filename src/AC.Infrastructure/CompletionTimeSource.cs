using System.Globalization;
using System.Text.RegularExpressions;

namespace AC.Infrastructure;

/// <summary>
/// Медиана времени до 100% по игре со страницы completionist.me (раздел 2.3, вариант А).
/// Публичного API у сайта нет, поэтому это разбор HTML: один запрос по кнопке, результат кешируется в игре.
/// Если вёрстка сайта поменяется - вернётся null, и время можно ввести вручную.
/// </summary>
public sealed partial class CompletionTimeSource(HttpClient http)
{
    public const string Name = "completionist.me";

    public async Task<double?> GetMedianMinutesAsync(uint appId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://completionist.me/steam/app/{appId}");
        req.Headers.UserAgent.ParseAdd($"AchievementCompanion/{typeof(CompletionTimeSource).Assembly.GetName().Version?.ToString(3)} (personal use)");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseMedian(html);
    }

    /// <summary>Ищет "Completion Playtime Median" и ближайшее за ним время вида "22h 20m".</summary>
    public static double? ParseMedian(string html)
    {
        var text = Tags().Replace(html, " ");
        text = Regex.Replace(text, @"\s+", " ");
        int idx = text.IndexOf("Completion Playtime Median", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // значение может стоять как до подписи, так и после - смотрим окно вокруг
        var window = text.Substring(Math.Max(0, idx - 80), Math.Min(text.Length - Math.Max(0, idx - 80), 240));
        var labelPos = window.IndexOf("Completion Playtime Median", StringComparison.OrdinalIgnoreCase);
        Match? best = null;
        int bestDist = int.MaxValue;
        foreach (Match m in Duration().Matches(window))
        {
            int d = Math.Abs(m.Index - labelPos);
            if (d < bestDist) { best = m; bestDist = d; }
        }
        if (best is null) return null;
        double h = best.Groups["h"].Success ? double.Parse(best.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        double mins = best.Groups["m"].Success ? double.Parse(best.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var total = h * 60 + mins;
        return total > 0 ? total : null;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"(?:(?<h>\d+)\s*h)\s*(?:(?<m>\d+)\s*m)?|(?<m>\d+)\s*m\b")]
    private static partial Regex Duration();
}
