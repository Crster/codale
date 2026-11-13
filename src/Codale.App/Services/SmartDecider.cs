namespace Codale.App.Services;

/// <summary>Sends questions to the Decision model and reports the request on the Smart status like any other.</summary>
internal static class SmartDecider
{
    public static bool IsAvailable => SmartExperienceHost.Decider is not null;

    /// <summary>The answers by question id; null when the Decision role is off or the request failed (the caller falls back).</summary>
    public static async Task<IReadOnlyDictionary<string, SmartDecision>?> AskAsync(object state,
        IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct)
    {
        if (SmartExperienceHost.Decider is not { } decider) return null;
        var id = Guid.NewGuid().ToString("N");
        SmartExperienceHost.Status.Start(id, SmartFeatureSettings.DecideTask);
        try
        {
            var answers = await decider.DecideAsync(state, questions, ct).ConfigureAwait(false);
            SmartExperienceHost.Status.Finish(id, "completed");
            return answers;
        }
        catch (OperationCanceledException)
        {
            SmartExperienceHost.Status.Finish(id, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SmartExperienceHost.Status.Finish(id, "error", ex.Message);
            return null;
        }
    }
}
