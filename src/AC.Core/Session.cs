namespace AC.Core;

/// <summary>A play session seen from its start: when the game was noticed running and when it closed.</summary>
public sealed record PlaySession(uint AppId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Follows the running game from tick to tick and reports the sessions seen from their start.</summary>
public sealed class SessionTracker(uint runningAtStart)
{
    private uint _running = runningAtStart;
    // A game already running when the app started has no known start, so its session is not reported.
    private DateTimeOffset? _start;

    /// <summary>The game running now (0 - none). Returns the session that has just ended.</summary>
    public PlaySession? Observe(uint running, DateTimeOffset now)
    {
        if (running == _running) return null;
        var ended = _running != 0 && _start is { } start ? new PlaySession(_running, start, now) : null;
        _running = running;
        _start = running != 0 ? now : null;
        return ended;
    }
}

/// <summary>What a session did: its unlocks in order and the time to 100% without and with them.</summary>
public sealed record SessionSummary(
    TimeSpan Duration,
    IReadOnlyList<AchievementDef> Unlocked,
    double? LeftBefore,
    double? LeftAfter,
    bool IsComplete)
{
    /// <summary>Steam's unlock times and the local clock may differ a little.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    public static SessionSummary Of(GameData game, PlaySession session, Percentile p)
    {
        var unlocked = game.Achievements
            .Select(a => (Def: a, State: game.States.GetValueOrDefault(a.ApiName)))
            .Where(x => x.State is { SteamUnlocked: true, SteamUnlockTime: { } t } &&
                t >= session.Start - ClockSlack && t <= session.End + ClockSlack)
            .OrderBy(x => x.State!.SteamUnlockTime)
            .ToList();
        var after = Planner.Build(game, p);
        foreach (var x in unlocked) x.State!.SteamUnlocked = false;
        Plan before;
        try { before = Planner.Build(game, p); }
        finally { foreach (var x in unlocked) x.State!.SteamUnlocked = true; }
        return new SessionSummary(session.End - session.Start, unlocked.Select(x => x.Def).ToList(),
            before.RemainingMinutes, after.RemainingMinutes, after.IsComplete);
    }
}
