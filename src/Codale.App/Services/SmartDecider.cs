using Codale.Agents.Providers;

namespace Codale.App.Services;

public interface ISmartDecider
{
    bool IsAvailable { get; }
    Task<IReadOnlyDictionary<string, SmartDecision>?> AskAsync(object state, IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct);
}

/// <summary>Sends questions to the Decision model and reports the request on the Smart status like any other.</summary>
public sealed class SmartDecider(SmartExperienceHost smart) : ISmartDecider
{
    private readonly SmartDecisionService _service = new(new SmartStatusSink(smart.Status));

    public bool IsAvailable => smart.Decider is not null;

    /// <summary>The answers by question id; null when the Decision role is off or the request failed (the caller falls back).</summary>
    public Task<IReadOnlyDictionary<string, SmartDecision>?> AskAsync(object state,
        IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct) =>
        _service.AskAsync(smart.Decider, state, questions, ct);
}
