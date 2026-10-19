using System.Globalization;

namespace AC.Core;

/// <summary>
/// Rarity model: the game's time to 100% is spread over the achievements
/// by their global percentage. The rarer an achievement, the later it is earned (and the longer it takes).
///
/// Time from the start of the game t = T * ln(maxPct / pct) / ln(maxPct / minPct),
/// where T is the time to 100%. The most common achievement gets about 0,
/// the rarest gets T. The logarithm is needed because percentages do not fall linearly:
/// there is usually less play between 90% and 45% than between 4% and 2%.
///
/// P75 and P90 are not statistics but a margin: x1.3 and x1.7 of P50 (marked in Source).
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
            // A measurement from the player sample is more accurate than the model: keep it.
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
    /// Time to 100% from the player's own progress (when sites have nothing, e.g. a new game).
    /// Same scale: if the "latest" earned achievement sits at fraction s of the scale
    /// and P is played, then 100% takes about P / s. Requires at least 30 minutes played
    /// and something past the very start (s from 0.15), otherwise extrapolation is meaningless.
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

    /// <summary>Time to 100% from own progress (see DeriveCompletion), or null if it is too early to tell.</summary>
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
        if (m < 1) return Loc.T("units.lessMinute");
        if (m < 60) return Loc.T("units.minutes", m.ToString("0", CultureInfo.CurrentCulture));
        var h = m / 60.0;
        return Loc.T("units.hours", h.ToString(h < 10 ? "0.#" : "0", CultureInfo.CurrentCulture));
    }
}
