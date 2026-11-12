using Codale.Core.Agents;

namespace Codale.Core.Text;

/// <summary>What an attachment pill holds; decides its icon and how it opens.</summary>
public enum PromptPillKind
{
    Image,
    Pdf,
    Text,
}

/// <summary>
/// A run of prompt text shown as one attachment pill: the "@file" mention an attachment puts in
/// the prompt. <see cref="Text"/> is the mention as written; <see cref="Title"/> is the file name.
/// </summary>
public sealed record PromptPill(int Start, int Length, PromptPillKind Kind, string Title, string Target)
{
    public string Text { get; init; } = "";

    public int End => Start + Length;
}

/// <summary>
/// Finds the "@file" mentions of attachable files (images, PDFs, text and code) in a prompt. The
/// prompt stays plain text: a pill is only a view of an attachment's mention, and callers decide
/// which mentions really are attachments.
/// </summary>
public static class PromptPills
{
    /// <summary>
    /// The mentions in <paramref name="text"/>, in order: "@name.ext" or "@\"name with spaces.ext\""
    /// starting a word, with an extension that can be attached. "@media" or a decorator like
    /// "@app.route" in pasted code is not one.
    /// </summary>
    public static IReadOnlyList<PromptPill> Find(string text)
    {
        var pills = new List<PromptPill>();
        var i = 0;
        while (i < text.Length)
        {
            var wordStart = i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] == '(';
            if (wordStart && TryMention(text, i, out var mention))
            {
                pills.Add(mention);
                i = mention.End;
                continue;
            }

            i++;
        }

        return pills;
    }

    /// <summary>The single pill <paramref name="text"/> is, when the whole of it (trimmed) reads as one.</summary>
    public static PromptPill? Whole(string text)
    {
        var trimmed = text.Trim();
        return Find(trimmed) is [{ Start: 0 } only] && only.Length == trimmed.Length ? only : null;
    }

    /// <summary>The "@name" mention for a path: quoted when it holds whitespace.</summary>
    public static string Mention(string path) =>
        "@" + (path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path);

    private static readonly char[] CodeCharacters = ['\'', '"', '(', ')', '[', ']', '{', '}', '<', '>', '|', '*', '?', '=', ';', ','];

    private static bool TryMention(string text, int start, out PromptPill pill)
    {
        pill = null!;
        if (text[start] != '@' || start + 1 >= text.Length)
        {
            return false;
        }

        string target;
        int end;
        if (text[start + 1] == '"')
        {
            var close = text.IndexOf('"', start + 2);
            if (close < 0 || text.IndexOfAny(['\r', '\n'], start + 2, close - start - 2) >= 0)
            {
                return false;
            }

            target = text[(start + 2)..close];
            end = close + 1;
        }
        else
        {
            end = start + 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            // Sentence punctuation after a typed mention is not part of the name.
            while (end > start + 1 && text[end - 1] is ',' or ';' or ':' or '!' or '?' or ')' or '.')
            {
                end--;
            }

            target = text[(start + 1)..end];

            // Code, not a file: "@app.route('/')", "@Html.Raw(x)".
            if (target.IndexOfAny(CodeCharacters) >= 0)
            {
                return false;
            }
        }

        if (target.Trim().Length == 0 || TurnAttachment.KindOf(target) is not { } attachable)
        {
            return false;
        }

        var kind = attachable switch
        {
            TurnAttachmentKind.Image => PromptPillKind.Image,
            TurnAttachmentKind.Pdf => PromptPillKind.Pdf,
            _ => PromptPillKind.Text,
        };

        var name = target[(target.LastIndexOfAny(['/', '\\']) + 1)..];
        pill = new PromptPill(start, end - start, kind, name.Length > 0 ? name : target, target) { Text = text[start..end] };
        return true;
    }
}
