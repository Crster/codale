using Codale.Db;

namespace Codale.App.Services;

/// <summary>Uses the configured OpenAI embedding model; disabled providers return no semantic neighbours.</summary>
public sealed class SmartEmbedder : IEmbedder
{
    private readonly SmartProviderClient _client = SmartExperienceHost.CreateClient("embedding");
    public string Model => SmartExperienceHost.CreateClient("embedding").EmbeddingIdentity == _client.EmbeddingIdentity ? _client.EmbeddingIdentity : "off";
    public float[] Embed(string text)
    {
        if (Model == "off") return [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var id = Guid.NewGuid().ToString("N");
        SmartExperienceHost.Status.Start(id, "embedding");
        try
        {
            var vector = _client.EmbedAsync(text, timeout.Token).GetAwaiter().GetResult();
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
