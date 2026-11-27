namespace PCOptimizer.Services.GameTools;

public enum WatchDecision { None, Activate, Deactivate }

/// <summary>Decides when to switch Game Mode on or off. No system access, so it can be tested.</summary>
public sealed class GameWatcher
{
    public const int PollsToStart = 2;   // ignores programs that exit at once
    public const int PollsToStop = 4;    // tolerates a game restarting itself

    private string? _candidate;
    private int _seen, _missing;
    private readonly HashSet<string> _suppressed = new();

    /// <summary>The game this session is following.</summary>
    public string? Attached { get; private set; }

    /// <summary>Call once per poll with the ids of running games that are allowed to trigger.</summary>
    public WatchDecision Poll(IReadOnlyList<string> running, bool active, bool autoActivate)
    {
        _suppressed.IntersectWith(running);   // forgotten once the game closes
        if (active)
        {
            if (Attached == null) { Attached = running.FirstOrDefault(); _missing = 0; return WatchDecision.None; }
            if (running.Contains(Attached)) { _missing = 0; return WatchDecision.None; }
            if (++_missing < PollsToStop) return WatchDecision.None;
            _missing = 0;
            Attached = running.FirstOrDefault();   // another game is still open: stay on
            return Attached == null ? WatchDecision.Deactivate : WatchDecision.None;
        }

        Attached = null;
        var next = autoActivate ? running.FirstOrDefault(id => !_suppressed.Contains(id)) : null;
        if (next == null) { (_candidate, _seen) = (null, 0); return WatchDecision.None; }
        _seen = next == _candidate ? _seen + 1 : 1;
        _candidate = next;
        if (_seen < PollsToStart) return WatchDecision.None;
        (_candidate, _seen, _missing, Attached) = (null, 0, 0, next);
        return WatchDecision.Activate;
    }

    /// <summary>The user turned Game Mode off: do not turn it back on for games that are still open.</summary>
    public void ManualOff(IEnumerable<string> running)
    {
        _suppressed.UnionWith(running);
        (Attached, _candidate, _seen, _missing) = (null, null, 0, 0);
    }
}
