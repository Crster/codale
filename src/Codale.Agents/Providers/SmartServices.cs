using Codale.Core.Agents;
using Codale.Core.Features;

namespace Codale.Agents.Providers;

/// <summary>
/// The Decision role's workflow: asks the decision model the questions and reports the request on the Smart status
/// like any other. A failed request gives null, so the caller falls back.
/// </summary>
public sealed class SmartDecisionService(ISmartRequestStatus status)
{
    /// <summary>The answers by question id; null when the decider is off or the request failed.</summary>
    public Task<IReadOnlyDictionary<string, SmartDecision>?> AskAsync(ISmartProviderClient? decider, object state,
        IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct)
    {
        if (decider is null)
        {
            return Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(null);
        }

        return SmartRequestWorkflow.RunTrackedAsync<IReadOnlyDictionary<string, SmartDecision>>(status,
            SmartFeatureSettings.DecideTask,
            async token => await decider.DecideAsync(state, questions, token).ConfigureAwait(false), ct);
    }
}

/// <summary>
/// The Embedding role's workflow: the configured vector model, or Codale's local hashed embedder while it is off.
/// A request that times out or fails is reported on the status and rethrown.
/// </summary>
public sealed class SmartEmbeddingService(ISmartRequestStatus status, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Whether a vector model, not the local hashed embedder, produces the vectors.</summary>
    public static bool UsesModel(ISmartProviderClient client) => client.EmbeddingIdentity != "off";

    /// <summary>The identity stored beside a vector: the model, or the hashed embedder's model while it is off.</summary>
    public static string ModelOf(ISmartProviderClient client) =>
        UsesModel(client) ? client.EmbeddingIdentity : HashedTextEmbedder.Instance.Model;

    public float[] Embed(ISmartProviderClient client, string text)
    {
        if (!UsesModel(client)) return HashedTextEmbedder.Instance.Embed(text);
        using var deadline = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var id = Guid.NewGuid().ToString("N");
        status.Start(id, "embedding");
        try
        {
            var vector = client.EmbedAsync(text, deadline.Token).GetAwaiter().GetResult();
            status.Finish(id, "completed");
            return vector;
        }
        catch (Exception ex)
        {
            status.Finish(id, "error", ex is OperationCanceledException
                ? "The embedding request timed out. Check the connection and model." : ex.Message);
            throw;
        }
    }
}
