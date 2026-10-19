namespace AC.Core;

/// <summary>One sampled player: what they unlocked when, and their playtime at known moments.</summary>
public sealed class PlayerSample
{
    public ulong SteamId { get; init; }
    /// <summary>Unlocked achievements: api name and unlock time.</summary>
    public Dictionary<string, DateTimeOffset> Unlocks { get; init; } = new();
    /// <summary>
    /// Anchor points "moment - minutes played": the review (its time and playtime at the review),
    /// the last launch (its time and total playtime). They give the playtime at each unlock.
    /// </summary>
    public List<(DateTimeOffset At, double PlayedMinutes)> Anchors { get; init; } = new();
}

public sealed record SampleResult(
    int Players,
    Dictionary<string, double> Percent,
    Dictionary<string, TimeEstimate> Times,
    double? CompletionMedianMinutes,
    int Completers);

/// <summary>
/// Our own statistics from a player sample (when Steam has no global percentages and sites have no completion times).
///
/// Percent = share of sampled players who unlocked the achievement.
/// Time: for each player we know when they unlocked the achievement and two playtime anchor points
/// (at the review, and the total at the last launch). Assuming even play between the points,
/// the unlock time is converted to "minutes played by then". Across players we take the median and percentiles.
/// This is an estimate: the sample is review authors, who usually play more than average.
/// </summary>
public static class SampleStats
{
    public const string SourceName = "sample";

    public static SampleResult Compute(IReadOnlyList<PlayerSample> players, IReadOnlyCollection<string> allApiNames)
    {
        var percent = new Dictionary<string, double>(StringComparer.Ordinal);
        var minutes = allApiNames.ToDictionary(n => n, _ => new List<double>(), StringComparer.Ordinal);
        var completions = new List<double>();
        int n = players.Count;

        foreach (var p in players)
        {
            if (p.Unlocks.Count == 0) continue;
            var curve = BuildCurve(p);
            foreach (var (name, at) in p.Unlocks)
            {
                if (!minutes.TryGetValue(name, out var list)) continue;
                var m = curve(at);
                if (m is not null) list.Add(m.Value);
            }
            if (allApiNames.Count > 0 && allApiNames.All(p.Unlocks.ContainsKey))
            {
                var last = p.Unlocks.Values.Max();
                if (curve(last) is { } done) completions.Add(done);
            }
        }

        foreach (var name in allApiNames)
        {
            int unlocked = players.Count(p => p.Unlocks.ContainsKey(name));
            if (n > 0) percent[name] = Math.Round(100.0 * unlocked / n, 1);
        }

        var times = new Dictionary<string, TimeEstimate>(StringComparer.Ordinal);
        foreach (var (name, list) in minutes)
        {
            if (list.Count < 2) continue;
            list.Sort();
            times[name] = new TimeEstimate
            {
                Source = SourceName,
                P50 = Math.Round(Quantile(list, 0.50), 1),
                P75 = Math.Round(Quantile(list, 0.75), 1),
                P90 = Math.Round(Quantile(list, 0.90), 1),
                SampleSize = list.Count,
            };
        }

        completions.Sort();
        double? completion = completions.Count >= 2 ? Math.Round(Quantile(completions, 0.5), 0) : null;
        return new SampleResult(n, percent, times, completion, completions.Count);
    }

    /// <summary>Minutes played by a given moment, from the anchor points (piecewise linear, 0 at the first unlock).</summary>
    internal static Func<DateTimeOffset, double?> BuildCurve(PlayerSample p)
    {
        var first = p.Unlocks.Values.Min();
        var pts = new List<(double T, double M)> { (first.ToUnixTimeSeconds(), 0) };
        foreach (var (at, played) in p.Anchors.Where(a => a.PlayedMinutes > 0).OrderBy(a => a.At))
        {
            var t = at.ToUnixTimeSeconds();
            if (t <= pts[^1].T) continue;
            pts.Add((t, Math.Max(pts[^1].M, played)));
        }
        if (pts.Count < 2) return _ => null; // nothing to anchor the time to

        return at =>
        {
            double t = at.ToUnixTimeSeconds();
            if (t <= pts[0].T) return 0;
            for (int i = 1; i < pts.Count; i++)
            {
                if (t <= pts[i].T)
                {
                    var (t0, m0) = pts[i - 1];
                    var (t1, m1) = pts[i];
                    return m0 + (m1 - m0) * (t - t0) / (t1 - t0);
                }
            }
            return pts[^1].M; // unlocked after the last point: known only to be at most the total playtime
        };
    }

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>Writes the result into the game: sample percentages, measured times, time to 100%.</summary>
    public static void Apply(GameData game, SampleResult r, DateTimeOffset now)
    {
        foreach (var a in game.Achievements)
            a.SamplePercent = r.Percent.TryGetValue(a.ApiName, out var pc) ? pc : null;
        foreach (var key in game.Estimates.Where(kv => kv.Value.Source == SourceName).Select(kv => kv.Key).ToList())
            game.Estimates.Remove(key);
        foreach (var (name, est) in r.Times) game.Estimates[name] = est;
        game.SampleCompletionMinutes = r.CompletionMedianMinutes;
        game.SamplePlayers = r.Players;
        game.SampleCompleters = r.Completers;
        game.SampleFetchedAt = now;
    }
}
