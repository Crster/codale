namespace Codale.Core.Agents;

/// <summary>How a turn ended, as the chat records it.</summary>
public enum TurnEnding
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>The notice a turn's end leaves in the transcript, if any.</summary>
public enum TurnNotice
{
    None,

    /// <summary>The reader interrupted the turn.</summary>
    Interrupted,

    /// <summary>Auto mode's safety check stopped responding; nothing it asked about ran.</summary>
    Paused,

    /// <summary>The turn ended with an error.</summary>
    Failed,
}

/// <summary>The outcome of a finished turn and the notice it shows.</summary>
public sealed record TurnResolution(TurnEnding Ending, TurnNotice Notice, string NoticeText)
{
    /// <summary>Decides how a turn that the CLI reported as finished ended.</summary>
    /// <param name="subtype">The CLI's result subtype; "success" when the turn completed.</param>
    /// <param name="firstError">The first error the CLI reported, if any.</param>
    /// <param name="interruptPending">True when the reader asked to interrupt this turn.</param>
    /// <param name="autoModeUnavailable">True when Auto mode's safety check was down during the turn.</param>
    public static TurnResolution Resolve(string subtype, string? firstError, bool interruptPending, bool autoModeUnavailable)
    {
        var failed = !string.Equals(subtype, "success", StringComparison.Ordinal);
        var interrupted = interruptPending && subtype == "error_during_execution";
        var paused = !interrupted && failed && autoModeUnavailable;

        var ending = !failed ? TurnEnding.Completed
            : interrupted || paused ? TurnEnding.Stopped
            : TurnEnding.Failed;

        return interrupted
            ? new(ending, TurnNotice.Interrupted, "Turn interrupted.")
            : paused
            ? new(ending, TurnNotice.Paused,
                "Claude paused: auto mode's safety check stopped responding, so the last actions didn't run. " +
                "Retry once it's back, or switch to another mode to keep going.")
            : failed ? new(ending, TurnNotice.Failed, $"Turn ended: {firstError ?? subtype}") : new(ending, TurnNotice.None, "");
    }
}