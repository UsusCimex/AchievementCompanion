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

        game.DerivedCompletionMinutes = null;

        var withPct = game.Achievements
            .Where(a => a.Percent is > 0)
            .ToList();
        if (withPct.Count == 0) return;

        double max = withPct.Max(a => a.Percent!.Value);
        double min = withPct.Min(a => a.Percent!.Value);
        double span = Math.Log(max / min);

        var total = game.CompletionMinutes is > 0 ? game.CompletionMinutes
            : game.SampleCompletionMinutes is > 0 ? game.SampleCompletionMinutes
            : null;
        if (total is null)
        {
            game.DerivedCompletionMinutes = DeriveCompletion(game, withPct, max, span);
            total = game.DerivedCompletionMinutes;
        }
        if (total is null || total <= 0) return;

        foreach (var a in withPct)
        {
            // Есть замер по выборке игроков - он точнее модели, не трогаем.
            if (game.Estimates.TryGetValue(a.ApiName, out var measured) && measured.Source == SampleStats.SourceName) continue;
            double share = span > 1e-9 ? Math.Log(max / a.Percent!.Value) / span : 0.5;
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

    /// <summary>
    /// Время до 100% по своему прогрессу (когда сайты молчат, например игра новая).
    /// По той же шкале: если самое "позднее" из полученных достижений стоит на доле s шкалы,
    /// а наиграно P, то до 100% примерно P / s. Нужно наиграть хотя бы 30 минут
    /// и получить что-то не из самого начала (s от 0.15), иначе экстраполяция бессмысленна.
    /// </summary>
    public static double? DeriveCompletion(GameData game, IReadOnlyList<AchievementDef> withPct, double max, double span)
    {
        var played = game.EffectivePlaytimeMinutes;
        if (played < 30 || span <= 1e-9) return null;
        double sMax = 0;
        foreach (var a in withPct)
        {
            if (GameLogic.StatusOf(game.States.GetValueOrDefault(a.ApiName), game.CountManualMarks) == EffectiveStatus.Locked) continue;
            sMax = Math.Max(sMax, Math.Log(max / a.Percent!.Value) / span);
        }
        if (sMax < 0.15) return null;
        return Math.Round(played / sMax, 0);
    }

    /// <summary>Время до 100% по своему прогрессу (см. DeriveCompletion) или null, если судить рано.</summary>
    public static double? PersonalCompletion(GameData game)
    {
        var withPct = game.Achievements.Where(a => a.Percent is > 0).ToList();
        if (withPct.Count == 0) return null;
        double max = withPct.Max(a => a.Percent!.Value);
        double min = withPct.Min(a => a.Percent!.Value);
        return DeriveCompletion(game, withPct, max, Math.Log(max / min));
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
