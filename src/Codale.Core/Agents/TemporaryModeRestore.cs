namespace Codale.Core.Agents;

/// <summary>
/// The host mode a temporary turn (a plan, or an Ask turn routed from Automatic) hands back when it
/// finishes. The session is captured with the mode, so a session replaced mid-turn is never touched.
/// Pure state: the caller performs the CLI switch that <see cref="Take"/> asks for.
/// </summary>
public sealed class TemporaryModeRestore<TSession> where TSession : class
{
    /// <summary>The session the temporary turn runs on, or null when no restore is pending.</summary>
    public TSession? Session { get; private set; }

    /// <summary>The host mode to hand back when the temporary turn ends.</summary>
    public string? RestoreMode { get; private set; }

    /// <summary>
    /// True while the Ask mode on screen is Automatic's, set for one routed question rather than picked by the
    /// reader. The next prompt hands Automatic back even when the end-of-turn restore never ran.
    /// </summary>
    public bool AskTurnFromAuto { get; set; }

    /// <summary>Starts a temporary turn on <paramref name="session"/>, remembering the mode it started from.</summary>
    /// <param name="session">The session the turn runs on.</param>
    /// <param name="currentMode">The host mode in effect when the turn starts.</param>
    /// <param name="fallbackMode">The mode to hand back when none is in effect.</param>
    public void Begin(TSession session, string currentMode, string fallbackMode)
    {
        Session = session;
        RestoreMode = currentMode is { Length: > 0 } mode ? mode : fallbackMode;
    }

    /// <summary>Forgets any pending restore.</summary>
    public void Clear()
    {
        Session = null;
        RestoreMode = null;
    }

    /// <summary>
    /// Ends the pending restore and returns what to hand back, or null when there is nothing to restore
    /// or the session has been replaced since the turn began (the stale restore is dropped).
    /// </summary>
    /// <param name="current">The session the chat is on now.</param>
    /// <param name="fallbackMode">The mode to hand back when none was recorded.</param>
    public (TSession Session, string Mode)? Take(TSession? current, string fallbackMode)
    {
        if (Session is not { } planned)
        {
            return null;
        }

        var mode = RestoreMode ?? fallbackMode;
        Clear();
        return current is not null && ReferenceEquals(current, planned) ? (planned, mode) : null;
    }
}