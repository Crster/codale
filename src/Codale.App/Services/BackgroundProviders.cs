using Codale.Agents.OpenRouter;
using Codale.Core.Agents;
using Codale.Core.Background;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>
/// The background-task provider as Settings configures it ("Background tasks"), resolved afresh on
/// every call: a change in Settings applies to the next job without restarting anything.
/// </summary>
public static class BackgroundProviders
{
    private static readonly Lock Gate = new();
    private static (string Id, OpenRouterSettings Settings, IBackgroundProvider Provider)? _cached;

    /// <summary>The provider background jobs run on now; <see cref="OffBackgroundProvider"/> when Settings says Off.</summary>
    public static IBackgroundProvider Current
    {
        get
        {
            var id = AppSettings.BackgroundProvider;
            var settings = AppSettings.OpenRouterSettings;
            lock (Gate)
            {
                if (_cached is { } cached && cached.Id == id && cached.Settings == settings)
                {
                    return cached.Provider;
                }

                IBackgroundProvider provider = id switch
                {
                    AppSettings.OpenRouterBackgroundProvider => new OpenRouterBackgroundProvider(settings),
                    _ => OffBackgroundProvider.Instance,
                };
                _cached = (id, settings, provider);
                return provider;
            }
        }
    }

    /// <summary>The fill-in-the-middle model for editor and shell ghost text, or null when none is set up.</summary>
    public static IFimCompleter? Completions => Current.Fim is { } fim ? new TrackedCompleter(fim) : null;

    /// <summary>A window's handle on the provider's chat model, with its own usage tally.</summary>
    public static BackgroundHelperModel Create() => new();
}

/// <summary>
/// A helper model that follows Settings: each request goes to the current provider's chat model.
/// One per window, so <see cref="RequestServed"/> counts only that window's requests (decisions included).
/// </summary>
public sealed class BackgroundHelperModel : IHelperModel
{
    /// <summary>Raised after each background request a provider answered, with what it reported spending.</summary>
    public event Action<UsageSnapshot>? RequestServed;

    private void OnServed(UsageSnapshot usage) => RequestServed?.Invoke(usage);

    private IHelperModel Inner => BackgroundProviders.Current.Helper(OnServed);

    /// <summary>The provider's native decision model for Automatic mode, or null to route on this chat model.</summary>
    public IDecisionModel? Decider => BackgroundProviders.Current.Decider(OnServed);

    /// <summary>How long a routing decision on the chat model may take before the router decides without it.</summary>
    public TimeSpan DecisionTimeout => BackgroundProviders.Current.DecisionTimeout;

    public bool IsAvailable => Inner.IsAvailable;

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default)
    {
        using var job = BackgroundWorkers.Track();
        return await Inner.CompleteAsync(systemPrompt, prompt, ct);
    }

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default)
    {
        using var job = BackgroundWorkers.Track();
        return await Inner.CompleteAsync(systemPrompt, prompt, maxTokens, ct);
    }

    public async Task<ToolCall?> CallToolAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default)
    {
        using var job = BackgroundWorkers.Track();
        return await Inner.CallToolAsync(systemPrompt, conversation, tools, ct);
    }

    public async Task<IReadOnlyList<ToolCall>> CallToolsAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default)
    {
        using var job = BackgroundWorkers.Track();
        return await Inner.CallToolsAsync(systemPrompt, conversation, tools, ct);
    }

    /// <summary>The model is about to be asked: get the provider's models loaded.</summary>
    public void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) => BackgroundProviders.Current.Prewarm();

    public void Prewarm() => BackgroundProviders.Current.Prewarm();
}

/// <summary>A ghost-text completer that counts each request as a background job while it is in flight.</summary>
internal sealed class TrackedCompleter(IFimCompleter inner) : IFimCompleter
{
    public async Task<string> CompleteAsync(string prefix, string suffix, IReadOnlyList<string>? rejected, CancellationToken ct = default)
    {
        using var job = BackgroundWorkers.Track();
        return await inner.CompleteAsync(prefix, suffix, rejected, ct);
    }
}
