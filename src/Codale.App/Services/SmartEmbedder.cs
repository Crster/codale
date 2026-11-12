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
        return _client.EmbedAsync(text, timeout.Token).GetAwaiter().GetResult();
    }
}
