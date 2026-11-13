using Codale.Core.Helper;
using Codale.Search;
using Codale.Smart;

namespace Codale.App.Services;

/// <summary>
/// The Smart roles behind Codale's tool-call style helpers: global search (keywords and the judge pass) and auto mode
/// routing. Each instance names its task, so the router picks Decision when set and General otherwise.
/// </summary>
public sealed class SmartHelperModel(string projectPath, string task, TimeSpan timeout) : IHelperModel, ISearchModel
{
    public bool IsAvailable => AppSettings.SmartFeatureEnabled(task) && SmartExperienceHost.Instance.Status.State == SmartState.Ready;

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        await CompleteAsync(systemPrompt, prompt, 512, ct).ConfigureAwait(false);

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default)
    {
        var result = await RunAsync(systemPrompt, prompt, maxTokens, text => string.IsNullOrWhiteSpace(text) ? null : text, ct).ConfigureAwait(false);
        return result ?? throw new HelperModelException("The Smart model gave no answer.");
    }

    public Task<ToolCall?> CallToolAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) =>
        RunAsync(HelperToolCalls.BuildSystemPrompt(systemPrompt, tools), conversation, 512, text => HelperToolCalls.Parse(text, tools), ct);

    Task<ToolCall?> ISearchModel.NextCallAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        CallToolAsync(systemPrompt, conversation, tools, ct);

    Task<string> ISearchModel.WriteAsync(string systemPrompt, string prompt, CancellationToken ct) =>
        CompleteAsync(systemPrompt, prompt, 1024, ct);

    void IHelperModel.Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) { }
    void ISearchModel.Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) { }

    private async Task<T?> RunAsync<T>(string system, string input, int cap, Func<string, T?> parse, CancellationToken ct) where T : class
    {
        if (!IsAvailable) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var result = await SmartExperienceHost.Instance.GenerateAsync(new(projectPath, projectPath, task),
                system, input, cap, parse, background: false, deadline.Token).ConfigureAwait(false);
            return result?.Value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
