namespace AC.Core;

/// <summary>
/// Оценка "вариант А": время до 100% по игре раскладывается по достижениям
/// по их глобальному проценту. Чем реже достижение, тем позже (и дольше) его получают.
///
/// Время от начала игры t = T * ln(maxPct / pct) / ln(maxPct / minPct),
/// где T - время до 100%. Самое частое достижение получает около 0,
/// самое редкое - T. Логарифм нужен, потому что проценты падают не линейно:
/// между 90% и 45% обычно меньше игры, чем между 4% и 2%.
///
/// P75 и P90 - не статистика, а запас: x1.3 и x1.7 от P50 (помечено в Source).
/// </summary>
public static class TimeEstimator
{
    public const string SourceName = "global-pct";
    public const double P75Factor = 1.3;
    public const double P90Factor = 1.7;

    public static void EstimateFromGlobalPercent(GameData game)
    {
        foreach (var key in game.Estimates.Where(kv => kv.Value.Source == SourceName).Select(kv => kv.Key).ToList())
            game.Estimates.Remove(key);

        var total = game.CompletionMinutes;
        if (total is null || total <= 0) return;

        var withPct = game.Achievements
            .Where(a => a.GlobalPercent is > 0)
            .ToList();
        if (withPct.Count == 0) return;

        double max = withPct.Max(a => a.GlobalPercent!.Value);
        double min = withPct.Min(a => a.GlobalPercent!.Value);
        double span = Math.Log(max / min);

        foreach (var a in withPct)
        {
            double share = span > 1e-9 ? Math.Log(max / a.GlobalPercent!.Value) / span : 0.5;
            double p50 = Math.Round(total.Value * share, 1);
            game.Estimates[a.ApiName] = new TimeEstimate
            {
                Source = SourceName,
                P50 = p50,
                P75 = Math.Round(p50 * P75Factor, 1),
                P90 = Math.Round(p50 * P90Factor, 1),
                SampleSize = 0,
            };
        }
    }

    public static string Format(double? minutes)
    {
        if (minutes is null) return "-";
        var m = minutes.Value;
        if (m < 1) return "<1 мин";
        if (m < 60) return $"{m:0} мин";
        var h = m / 60.0;
        return h < 10 ? $"{h:0.#} ч" : $"{h:0} ч";
    }
}
