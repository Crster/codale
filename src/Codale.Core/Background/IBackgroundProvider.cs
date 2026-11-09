using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Core.Background;

/// <summary>
/// What background jobs run on, as Settings picks it ("Background tasks"): a chat model for the helper
/// jobs, a decision model for Automatic mode, an embedding model for project retrieval and a
/// fill-in-the-middle model for ghost text. Each capability may be missing; callers fall back to their
/// non-model behaviour. Ollama is the first native provider; hosted ones (OpenRouter, Qwen Cloud) slot
/// in beside it.
/// </summary>
public interface IBackgroundProvider
{
    /// <summary>The stored settings value: "off", "ollama".</summary>
    string Id { get; }

    /// <summary>
    /// The chat model for titles, commit messages, summaries, search and editor actions, and the
    /// fallback when there is no decision model. Its <see cref="IHelperModel.IsAvailable"/> is false when none is set up.
    /// </summary>
    /// <param name="onUsage">Told what each answered request spent.</param>
    IHelperModel Helper(Action<UsageSnapshot>? onUsage = null);

    /// <summary>The native decision model for Automatic mode's routing, or null to route with <see cref="Helper"/>.</summary>
    IDecisionModel? Decider(Action<UsageSnapshot>? onUsage = null);

    /// <summary>How long a decision on the chat model may take before the caller decides without it.</summary>
    TimeSpan DecisionTimeout { get; }

    /// <summary>The embedding model for project retrieval, or null to use only the local hashed vectors.</summary>
    ITextEmbedder? Embedder { get; }

    /// <summary>The fill-in-the-middle model for editor and shell ghost text, or null for none.</summary>
    IFimCompleter? Fim { get; }

    /// <summary>A hint that background jobs are coming: load the models that answer on the user's critical path. Never blocks.</summary>
    void Prewarm();
}

/// <summary>Background tasks are off: nothing runs, nothing is loaded.</summary>
public sealed class OffBackgroundProvider : IBackgroundProvider
{
    public static readonly OffBackgroundProvider Instance = new();

    public const string ProviderId = "off";

    public string Id => ProviderId;

    public IHelperModel Helper(Action<UsageSnapshot>? onUsage = null) => UnavailableHelperModel.Instance;

    public IDecisionModel? Decider(Action<UsageSnapshot>? onUsage = null) => null;

    public TimeSpan DecisionTimeout => TimeSpan.FromSeconds(5);

    public ITextEmbedder? Embedder => null;

    public IFimCompleter? Fim => null;

    public void Prewarm()
    {
    }
}

/// <summary>The helper model of a provider that has none: never available, and says so if asked anyway.</summary>
public sealed class UnavailableHelperModel : IHelperModel
{
    public static readonly UnavailableHelperModel Instance = new();

    public bool IsAvailable => false;

    public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        Task.FromException<string>(new HelperModelException("Background tasks are off."));

    public Task<ToolCall?> CallToolAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) =>
        Task.FromResult<ToolCall?>(null);
}
