using Codale.Core.Helper;

namespace Codale.Core.Syntax;

/// <summary>A grammar the model wrote that passed validation, ready for the user to preview and save.</summary>
public sealed record GeneratedGrammar(string Json, GrammarMeta Meta, string Name, string[] Extensions);

/// <summary>
/// Has the helper model write a TextMate grammar for a file type. The reply is untrusted text:
/// the JSON is cut out of any prose or fences, validated, and trial-tokenized against the sample
/// the model was shown. If that fails the model gets one chance to fix it with the error, so the
/// user only ever sees a grammar that loads.
/// </summary>
public sealed class SyntaxGenerator
{
    private const int MaxSampleChars = 6000;
    private const int MaxReplyTokens = 16_000;

    private const string SystemPrompt = """
        You write TextMate grammars (.tmLanguage.json) for a code editor's syntax highlighting.
        Input: the language, its file extension and a sample file inside <sample>.
        Output: ONE JSON object and nothing else - no markdown fence, no commentary before or after it.

        Grammar rules:
        - Include "name", "scopeName" (source.<lowercase-id>), "fileTypes" (extensions without dots), "patterns" and, when useful, "repository".
        - Colour comments, strings, numbers, keywords, types, constants, operators and function names.
        - Use only standard scope names, so the editor can colour them: comment.line / comment.block, string.quoted.double / string.quoted.single, constant.numeric, constant.language, constant.character.escape, keyword.control, keyword.operator, keyword.other, storage.type, storage.modifier, entity.name.function, entity.name.type, entity.name.tag, entity.other.attribute-name, support.function, support.type, variable.language, markup.heading.
        - Prefer one "match" with a \b(word|word)\b alternation per keyword group. Use "begin"/"end" for block comments and multi-line strings.
        - Regular expressions use Oniguruma syntax, escaped for JSON ("\\b", "\\d"). Avoid nested quantifiers and anything that backtracks catastrophically.
        - Keep it compact: under 150 lines. Do not invent features the language does not have.

        Rules:
        """ + "\n" + PromptRules.DataOnly + "\n" + PromptRules.ResultOnly;

    private readonly IHelperModel _helper;

    public SyntaxGenerator(IHelperModel helper) => _helper = helper;

    public bool IsAvailable => _helper.IsAvailable;

    /// <summary>
    /// Generates and validates a grammar for <paramref name="languageName"/>. Throws
    /// <see cref="HelperModelException"/> when the model cannot be reached and
    /// <see cref="InvalidDataException"/> when it could not produce a usable grammar.
    /// </summary>
    public async Task<GeneratedGrammar> GenerateAsync(
        string languageName,
        string extension,
        string sample,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var ext = extension.Trim().TrimStart('.').ToLowerInvariant();
        var shownSample = sample.Length > MaxSampleChars ? sample[..MaxSampleChars] : sample;
        var prompt = $"""
            Language: {languageName}
            File extension: .{ext}

            {PromptRules.Tag("sample", shownSample)}

            Write the TextMate grammar for {languageName}.
            """;

        string? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(attempt == 1 ? "Asking the model for a grammar…" : "Asking the model to fix the grammar…");

            var ask = lastError is null
                ? prompt
                : prompt + $"\n\nYour previous reply was rejected: {lastError}\nReply again with a corrected grammar.";
            var reply = await _helper.CompleteAsync(SystemPrompt, ask, MaxReplyTokens, ct).ConfigureAwait(false);

            progress?.Invoke("Checking the grammar…");
            var json = ExtractJson(reply);
            if (json is null)
            {
                lastError = "the reply contained no JSON object.";
                continue;
            }

            var error = GrammarValidator.Validate(json, out var meta, sample: TrialSample(shownSample));
            if (error is null)
            {
                var name = meta.Name is { Length: > 0 } declared ? declared : languageName;
                var extensions = new[] { ext }.Concat(meta.FileTypes.Select(t => t.TrimStart('.').ToLowerInvariant()))
                    .Where(e => e.Length > 0)
                    .Distinct()
                    .ToArray();
                return new GeneratedGrammar(json, meta, name, extensions);
            }

            lastError = error;
        }

        throw new InvalidDataException($"The model could not produce a usable grammar: {lastError}");
    }

    /// <summary>The first complete JSON object in a reply, ignoring fences and any prose around it.</summary>
    public static string? ExtractJson(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }

    private static string TrialSample(string sample)
    {
        // A few real lines are a better trial than the built-in one, but a huge sample would only slow the check.
        var lines = sample.Split('\n').Take(40);
        return string.Join('\n', lines);
    }
}
