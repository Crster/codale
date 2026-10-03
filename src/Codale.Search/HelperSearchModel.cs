using Codale.Core.Helper;

namespace Codale.Search;

/// <summary>
/// Backs <see cref="SearchAgentLoop"/> and <see cref="CodeDiscovery"/> with the helper
/// model, so the loop runs in the app - where the UI traces it and where its grep and
/// file tools live - while each generation is a one-shot agent CLI call.
/// </summary>
public sealed class HelperSearchModel(IHelperModel model) : ISearchModel
{
    public Task<ToolCall?> NextCallAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct) =>
        model.CallToolAsync(systemPrompt, conversation, tools, ct);

    public Task<IReadOnlyList<ToolCall>> NextCallsAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct) =>
        model.CallToolsAsync(systemPrompt, conversation, tools, ct);

    public Task<string> WriteAsync(string systemPrompt, string prompt, CancellationToken ct) =>
        model.CompleteAsync(systemPrompt, prompt, ct);

    public void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) =>
        model.Prewarm(systemPrompt, tools);
}
