namespace AgentsCursor.Core;

/// <summary>The built-in chimes (short, soft WAVs the tray app embeds), or none.</summary>
public enum Chime
{
    None,
    /// <summary>One soft bell note.</summary>
    Bell,
    /// <summary>Two notes rising a fifth, like a question: the "Needs you" default.</summary>
    Rise,
    /// <summary>Two notes falling to rest: the "Done" default.</summary>
    Settle,
    /// <summary>A quick, quiet three-note arpeggio.</summary>
    Glow,
}

/// <summary>
/// When to play a chime: the pure half of the sounds, so it is unit-tested directly; the tray plays
/// what <see cref="Next"/> returns.
///
/// A chime plays once when a session <b>newly</b> starts waiting (<see cref="Arrivals"/>): it wasn't
/// waiting, or it moves from finished to blocked. A blocked arrival plays the "Needs you" chime, else a
/// finished one plays "Done". Nothing plays while the token is hidden (turned off, an app on the list
/// in front, a meeting), and a burst of arrivals within <see cref="Burst"/> of a chime is that chime:
/// one sound, not one per session; except that a "Needs you" still plays right after a "Done", since
/// it is the one that asks something of you. One instance per player; call it on one thread.
/// </summary>
public sealed class SoundCues
{
    /// <summary>Arrivals this soon after a chime are part of it and play nothing more.</summary>
    public static readonly TimeSpan Burst = TimeSpan.FromSeconds(1);

    DateTime? _lastChime;
    bool _burstNeedsYou; // the last chime was "Needs you"

    /// <summary>The waiting states the sessions in <paramref name="after"/> newly entered since
    /// <paramref name="before"/>, one per such session: a session that wasn't waiting, or that went
    /// from finished to blocked. Sessions are matched by <see cref="WaitingAgent.Session"/>.</summary>
    public static IReadOnlyList<WaitingState> Arrivals(IReadOnlyList<WaitingAgent> before, IReadOnlyList<WaitingAgent> after)
    {
        var was = new Dictionary<string, WaitingState>(StringComparer.Ordinal);
        foreach (var agent in before) was[agent.Session] = agent.State;
        var arrivals = new List<WaitingState>();
        foreach (var agent in after)
        {
            bool waited = was.TryGetValue(agent.Session, out var state);
            if (!waited || (state == WaitingState.Finished && agent.State == WaitingState.Blocked)) arrivals.Add(agent.State);
        }
        return arrivals;
    }

    /// <summary>The chime to play now that the waiting set changed from <paramref name="before"/> to
    /// <paramref name="after"/>, or <see cref="Chime.None"/>. <paramref name="tokenShown"/> says whether
    /// the token is on screen for the new set. A chime that plays starts a new <see cref="Burst"/>.</summary>
    public Chime Next(IReadOnlyList<WaitingAgent> before, IReadOnlyList<WaitingAgent> after, bool tokenShown,
                      CursorSettings settings, DateTime nowUtc)
    {
        if (!settings.SoundsEnabled || !tokenShown || settings.SoundVolume <= 0) return Chime.None;
        var arrivals = Arrivals(before, after);
        if (arrivals.Count == 0) return Chime.None;
        // A blocked arrival outranks a finished one; a state whose chime is None gives way to the other.
        bool needsYou = arrivals.Contains(WaitingState.Blocked) && settings.NeedsYouSound != Chime.None;
        // Within a burst only "Needs you" breaks through, and only a burst that so far was "Done".
        if (_lastChime is { } last && nowUtc - last < Burst && (_burstNeedsYou || !needsYou)) return Chime.None;

        var chime = needsYou ? settings.NeedsYouSound
            : arrivals.Contains(WaitingState.Finished) ? settings.DoneSound : Chime.None;
        if (chime != Chime.None)
        {
            _lastChime = nowUtc;
            _burstNeedsYou = needsYou;
        }
        return chime;
    }
}
