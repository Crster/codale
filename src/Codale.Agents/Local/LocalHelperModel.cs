using System.Text.Json;

using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Agents.Local;

/// <summary>
/// Answers <see cref="IHelperModel"/> requests from the fine-tuned model running in-process. Single-tool
/// calls use the trained arguments-only format under a grammar, so the reply always parses.
/// </summary>
public sealed class LocalHelperModel(Func<string?> modelPath) : IHelperModel, IDisposable
{
    private readonly Lock _lock = new();
    private LocalModelHost? _host;

    public bool IsAvailable => Host() is { } host && host.Problem() is null;

    private LocalModelHost? Host()
    {
        var path = modelPath()?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (_lock)
        {
            if (_host is null || !string.Equals(_host.ModelPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _host?.Dispose();
                _host = new LocalModelHost(path);
            }

            return _host;
        }
    }

    private LocalModelHost RequireHost() =>
        Host() ?? throw new HelperModelException("No local model file is set.");

    public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        CompleteAsync(systemPrompt, prompt, 0, ct);

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default) =>
        ModelOutput.Clean(await RequireHost()
            .GenerateAsync(CodalePromptFormat.Chat(systemPrompt, prompt), maxTokens, null, ct).ConfigureAwait(false));

    public async Task<ToolCall?> CallToolAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        var host = RequireHost();
        if (tools is [{ } tool] && CodalePromptFormat.Supports(tool))
        {
            var reply = await host.GenerateAsync(
                CodalePromptFormat.Chat(CodalePromptFormat.ToolSystem(systemPrompt, tool), conversation),
                0, CodalePromptFormat.ToolGrammar(tool), ct).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(reply);
                return new ToolCall(tool.Name, document.RootElement.Clone());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Not a shape the model was trained on: best effort with the generic contract.
        var generic = await host.GenerateAsync(
            CodalePromptFormat.Chat(HelperToolCalls.BuildSystemPrompt(systemPrompt, tools), conversation),
            0, null, ct).ConfigureAwait(false);
        return HelperToolCalls.Parse(generic, tools);
    }

    /// <summary>Characters of file text before the caret sent to the model: about the 1500 tokens it was trained with.</summary>
    private const int MaxPrefixChars = 4500;

    /// <summary>
    /// Ghost text for the caret: the next few tokens after <paramref name="prefix"/>, which ends at the caret.
    /// Empty when the model has nothing to add. Cancel it when the user types again.
    /// </summary>
    public async Task<string> CompleteCodeAsync(string path, string language, string prefix, CancellationToken ct)
    {
        if (prefix.Length > MaxPrefixChars)
        {
            // Cut at a line start so the model never sees half a line it cannot place.
            var cut = prefix.IndexOf('\n', prefix.Length - MaxPrefixChars);
            prefix = cut < 0 ? prefix[^MaxPrefixChars..] : prefix[(cut + 1)..];
        }

        return await RequireHost().CompleteInlineAsync(CodalePromptFormat.CodeComplete(path, language, prefix), 64, ct).ConfigureAwait(false);
    }

    /// <summary>The characters that finish <paramref name="typed"/> into the likeliest shell command; empty for no suggestion.</summary>
    public async Task<string> CompleteShellAsync(
        string shell, string cwd, IEnumerable<string> recent, IEnumerable<string> files, string typed, CancellationToken ct) =>
        (await RequireHost().CompleteInlineAsync(
            CodalePromptFormat.ShellComplete(shell, cwd, recent, files, typed), 48, ct).ConfigureAwait(false)).TrimEnd();

    public void Dispose() => _host?.Dispose();
}
