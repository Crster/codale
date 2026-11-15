namespace Codale.Core.Agents;

/// <summary>
/// The reasoning effort a plan turn runs at, and the effort the chat returns to once planning ends.
/// Planning twice in a row keeps the first turn's starting effort. Lite is promoted to Flash's effort
/// for planning; other tiers plan at high effort. Pure state: the caller applies the effort to the CLI.
/// </summary>
public sealed class PlanEffortOverride
{
    private string? _restoreEffort;

    /// <summary>True while a planning override is in place and not yet restored.</summary>
    public bool IsRaised { get; private set; }

    /// <summary>True when the planning turn started from the Lite tier, so it plans at Flash's effort.</summary>
    public bool PromotedFromLite { get; private set; }

    /// <summary>
    /// Starts planning: records the starting effort on the first call, and returns the effort to plan at.
    /// </summary>
    /// <param name="currentTier">The tier the chat is on when planning starts.</param>
    /// <param name="requestedEffort">The effort the chat requested before planning.</param>
    /// <param name="flashEffort">The effort Flash runs at; Lite plans at this.</param>
    public string? Enter(string currentTier, string? requestedEffort, string? flashEffort)
    {
        if (!IsRaised)
        {
            PromotedFromLite = currentTier == "lite";
            _restoreEffort = requestedEffort;
            IsRaised = true;
        }

        return PromotedFromLite ? flashEffort : "high";
    }

    /// <summary>
    /// The effort the tier label should show: the restored one while a planning override is in place,
    /// except for Lite, which shows the requested effort it was promoted from.
    /// </summary>
    public string? ShownEffort(string? requestedEffort) =>
        IsRaised && !PromotedFromLite ? _restoreEffort : requestedEffort;

    /// <summary>Ends the override and returns the effort to restore. Only meaningful while <see cref="IsRaised"/>.</summary>
    public string? Restore()
    {
        var effort = _restoreEffort;
        PromotedFromLite = false;
        IsRaised = false;
        _restoreEffort = null;
        return effort;
    }
}