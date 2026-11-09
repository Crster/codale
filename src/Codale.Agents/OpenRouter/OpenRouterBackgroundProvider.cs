using Codale.Core.Agents;
using Codale.Core.Background;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Agents.OpenRouter;

/// <summary>The OpenRouter settings background tasks run on. Blank model names mean "not set".</summary>
/// <param name="Reasoning">Whether reasoning models think before answering; slower, sometimes better.</param>
public sealed record OpenRouterSettings(
    string ApiKey,
    string Model,
    string EmbeddingModel,
    string CompletionModel,
    bool Reasoning);

/// <summary>Background tasks on OpenRouter: a chat model, and optionally embeddings and ghost text, each with its own model.</summary>
public sealed class OpenRouterBackgroundProvider : IBackgroundProvider
{
    public const string ProviderId = "openrouter";

    private static readonly TimeSpan DecisionLimit = TimeSpan.FromSeconds(6);

    private readonly OpenRouterClient _client;

    public OpenRouterBackgroundProvider(OpenRouterSettings settings, HttpClient? http = null)
    {
        Settings = settings;
        _client = new OpenRouterClient(settings.ApiKey, http: http);
        Embedder = _client.HasKey && Clean(settings.EmbeddingModel) is { Length: > 0 } embedding ? new OpenRouterEmbedder(_client, embedding) : null;
        Fim = _client.HasKey && Clean(settings.CompletionModel) is { Length: > 0 } completion ? new OpenRouterFimCompleter(_client, completion) : null;
    }

    public OpenRouterSettings Settings { get; }

    public string Id => ProviderId;

    public IHelperModel Helper(Action<UsageSnapshot>? onUsage = null) =>
        new OpenRouterHelperModel(_client, Clean(Settings.Model), Settings.Reasoning, onUsage);

    /// <summary>OpenRouter has no native decision model: Automatic mode routes on the chat model.</summary>
    public IDecisionModel? Decider(Action<UsageSnapshot>? onUsage = null) => null;

    public TimeSpan DecisionTimeout => DecisionLimit;

    public ITextEmbedder? Embedder { get; }

    public IFimCompleter? Fim { get; }

    /// <summary>Hosted models are always warm.</summary>
    public void Prewarm()
    {
    }

    private static string Clean(string? model) => model?.Trim() ?? "";
}
