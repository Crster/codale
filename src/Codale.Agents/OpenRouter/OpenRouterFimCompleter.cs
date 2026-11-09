using Codale.Agents.Claude;
using Codale.Core.Background;
using Codale.Core.Helper;

namespace Codale.Agents.OpenRouter;

/// <summary>
/// Ghost text from a fill-in-the-middle model on OpenRouter. The code before the caret is the
/// <c>prompt</c> and the code after it the <c>suffix</c> of the native <c>/completions</c> endpoint, and
/// the model writes the gap. Models served only as chat (no infill route) get their infill tokens inside a
/// chat request instead, when the model family is known (<see cref="FimTokens"/>).
/// </summary>
public sealed class OpenRouterFimCompleter(OpenRouterClient client, string model) : IFimCompleter
{
    private const int MaxTokens = 64;
    private const int MaxRetries = 2;
    private const int PrefixChars = 4000;
    private const int SuffixChars = 1000;

    private const string ChatSystem =
        "Write the code that belongs between the given prefix and suffix. Reply with ONLY that code, " +
        "no explanation and no code fences.";

    public string Model => model;

    public async Task<string> CompleteAsync(string prefix, string suffix, IReadOnlyList<string>? rejected, CancellationToken ct = default)
    {
        var seen = rejected ?? [];
        var before = prefix.Length > PrefixChars ? prefix[^PrefixChars..] : prefix;
        var after = suffix.Length > SuffixChars ? suffix[..SuffixChars] : suffix;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var another = seen.Count > 0 || attempt > 0;
            var raw = await FillAsync(before, after, another ? 0.7 : 0.2, ct).ConfigureAwait(false);
            var line = FimText.FirstLine(raw, suffix);
            if (line.Length == 0 || !seen.Contains(line, StringComparer.Ordinal))
            {
                return line;
            }
        }

        return "";
    }

    private async Task<string> FillAsync(string before, string after, double temperature, CancellationToken ct)
    {
        try
        {
            return await client.FillAsync(model, before, after, MaxTokens, temperature, ct).ConfigureAwait(false);
        }
        catch (HelperModelException ex) when (ex.StatusCode is 400 or 404 or 405 && FimTokens.Wrap(model, before, after) is not null)
        {
            // No infill route for this model: the same gap, asked for as chat with the model's own infill tokens.
            using var reply = await client.ChatAsync(
                new OpenRouterChatRequest(model, ChatSystem, FimTokens.Wrap(model, before, after)!, MaxTokens, Reasoning: false, Temperature: temperature),
                ct).ConfigureAwait(false);
            return OpenRouterClient.Message(reply.RootElement)?.Str("content") ?? "";
        }
    }
}

/// <summary>
/// The infill tokens each model family was trained with, for chat endpoints that cannot take a suffix.
/// Matched on the model id, so an unknown family returns null and gets no fallback.
/// </summary>
internal static class FimTokens
{
    /// <summary>Wraps the code around the caret in the family's infill tokens, or returns null for a family not listed.</summary>
    public static string? Wrap(string model, string before, string after)
    {
        var id = model.ToLowerInvariant();
        if (id.Contains("codestral", StringComparison.Ordinal) || id.Contains("mistral", StringComparison.Ordinal))
        {
            return $"[PREFIX]{before}[SUFFIX]{after}[MIDDLE]";
        }

        if (id.Contains("deepseek", StringComparison.Ordinal))
        {
            // The fullwidth bar in the tokens is part of the vocabulary, not a typo.
            return $"<｜fim begin｜>{before}<｜fim hole｜>{after}<｜fim end｜>";
        }

        if (id.Contains("starcoder", StringComparison.Ordinal))
        {
            return $"<fim_prefix>{before}<fim_suffix>{after}<fim_middle>";
        }

        if (id.Contains("qwen", StringComparison.Ordinal) && id.Contains("coder", StringComparison.Ordinal))
        {
            return $"<|fim_prefix|>{before}<|fim_suffix|>{after}<|fim_middle|>";
        }

        return null;
    }
}
