namespace Codale.Agents.Providers;

/// <summary>Sends each Smart task to the first configured model role it can run on (Decision falls back to General, and so on).</summary>
public sealed class SmartFeatureRouter(IReadOnlyDictionary<string, ISmartProviderClient> clients)
{
    public string ConfigurationKey => string.Join("|", clients.OrderBy(p => p.Key).Select(p => p.Value.ConfigurationKey));
    public bool HasGeneration => SmartFeatureSettings.Features.Any(f => f.Id != "embedding" && IsFeatureEnabled(f.Id));

    /// <summary>The client that serves <paramref name="task"/>, or null when none of its roles is configured.</summary>
    public ISmartProviderClient? ClientFor(string task) => SmartFeatureSettings.RolesFor(task)
        .Select(role => clients.GetValueOrDefault(role)).FirstOrDefault(client => client is { IsEnabled: true });

    /// <summary>Requests on different roles may run side by side; a fast Decision call never waits behind a General one.</summary>
    public string LaneFor(string task) => ClientFor(task)?.Role ?? SmartFeatureSettings.General;

    /// <summary>Whether a model can write this task's answer (chat, FIM or embedding); classification may also go to <see cref="Decider"/>.</summary>
    public bool IsFeatureEnabled(string task) => ClientFor(task) is not null;

    /// <summary>The Decision role's client when it is configured: a decision model that answers classification tasks.</summary>
    public ISmartProviderClient? Decider => clients.GetValueOrDefault(SmartFeatureSettings.Decision) is { IsEnabled: true } decider ? decider : null;

    /// <summary>Whether the Decision model answers this task.</summary>
    public bool Decides(string task) => SmartFeatureSettings.IsDecisionTask(task) && Decider is not null;

    public Task<string> CompleteAsync(string task, string system, string input, int tokens, CancellationToken ct) =>
        (ClientFor(task) ?? throw new InvalidOperationException("This Smart feature is off."))
            .CompleteAsync(task, system, input, tokens, ct);
}