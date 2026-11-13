using Codale.Core.Features;
using Codale.Db;

namespace Codale.App.Services;

/// <summary>
/// The Embedding role: the configured vector model, or Codale's local hashed embedder while it is off.
/// The model id is stored beside every vector, so changing the setting re-embeds lazily without reopening the project.
/// </summary>
public sealed class SmartEmbedder : IEmbedder
{
    public static SmartEmbedder Shared { get; } = new();

    private volatile SmartProviderClient _client;

    private SmartEmbedder()
    {
        _client = SmartExperienceHost.CreateClient(SmartFeatureSettings.Embedding);
        AppSettings.Changed += (_, _) => _client = SmartExperienceHost.CreateClient(SmartFeatureSettings.Embedding);
    }

    /// <summary>Whether a vector model (not the local hashed embedder) produces the vectors.</summary>
    public bool UsesModel => _client.EmbeddingIdentity != "off";

    public string Model => _client.EmbeddingIdentity is var identity && identity != "off" ? identity : HashedTextEmbedder.Instance.Model;

    public float[] Embed(string text)
    {
        var client = _client;
        if (client.EmbeddingIdentity == "off") return HashedTextEmbedder.Instance.Embed(text);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var id = Guid.NewGuid().ToString("N");
        SmartExperienceHost.Status.Start(id, "embedding");
        try
        {
            var vector = client.EmbedAsync(text, timeout.Token).GetAwaiter().GetResult();
            SmartExperienceHost.Status.Finish(id, "completed");
            return vector;
        }
        catch (Exception ex)
        {
            SmartExperienceHost.Status.Finish(id, "error", ex is OperationCanceledException
                ? "The embedding request timed out. Check the connection and model." : ex.Message);
            throw;
        }
    }
}
