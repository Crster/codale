namespace Codale.Core.Text;

/// <summary>Turns a fill-in-the-middle model's raw output into the one line the caret gets.</summary>
public static class FimText
{
    private static readonly System.Buffers.SearchValues<char> Punctuation =
        System.Buffers.SearchValues.Create(")]}>;,.:'\"` ");

    /// <summary>
    /// The first line of <paramref name="raw"/>, without any part of it that is already written right after
    /// the caret: a model often re-emits the closing <c>);</c> it was shown in the suffix.
    /// </summary>
    /// <param name="prefix">The text before the caret: when its line already ends in whitespace (an indent, a space after
    /// <c>=</c>), the proposal's own leading whitespace would double it and is dropped.</param>
    public static string FirstLine(string raw, string suffix, string? prefix = null)
    {
        // A model asked to fill an empty line often opens with a line break; the proposal is the first line with text.
        var text = raw.Replace("\r\n", "\n").TrimStart('\n');
        var newline = text.IndexOf('\n');
        if (newline >= 0)
        {
            text = text[..newline];
        }

        text = text.TrimEnd();
        if (prefix is { Length: > 0 } && prefix[^1] is ' ' or '\t')
        {
            text = text.TrimStart(' ', '\t');
        }
        if (text.Trim().Length == 0)
        {
            return "";
        }

        // The rest of the caret's line, as it stands: whatever the proposal ends with that this starts with is a repeat.
        var after = suffix.Replace("\r\n", "\n");
        var end = after.IndexOf('\n');
        var restOfLine = (end >= 0 ? after[..end] : after).TrimEnd();
        if (restOfLine.Length > 0)
        {
            for (var length = Math.Min(text.Length, restOfLine.Length); length > 0; length--)
            {
                // A short overlap of letters is as likely chance ("a + b" before "b)") as a repeat;
                // closing punctuation ("));") never is.
                var overlap = restOfLine.AsSpan(0, length);
                if (string.CompareOrdinal(text, text.Length - length, restOfLine, 0, length) == 0 &&
                    (length >= 3 || !overlap.ContainsAnyExcept(Punctuation)))
                {
                    text = text[..^length].TrimEnd();
                    break;
                }
            }
        }

        return text.Trim().Length == 0 ? "" : text;
    }

    /// <summary>
    /// True when <paramref name="completion"/> reads as a chat reply rather than code: an instruct model sometimes
    /// answers the caret with "It looks like you're...". Such text is never shown as a completion.
    /// </summary>
    public static bool LooksLikeProse(string completion)
    {
        var text = completion.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        string[] markers = ["it looks like", "you're", "you are", "i'll", "i can", "here's", "here is", "let me", "sure,", "this is", "i notice", "based on", "<think>", "```"];
        if (markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 6 && (text.EndsWith(':') || text.Contains(". ", StringComparison.Ordinal) || text.EndsWith('.'));
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="maxChars"/> from its end (or start, with
    /// <paramref name="fromEnd"/> false), at a line boundary where one is near, so the model never sees
    /// half a line it cannot place.
    /// </summary>
    public static string Clip(string text, int maxChars, bool fromEnd)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        if (fromEnd)
        {
            var cut = text.IndexOf('\n', text.Length - maxChars);
            return cut < 0 ? text[^maxChars..] : text[(cut + 1)..];
        }

        var last = text.LastIndexOf('\n', maxChars - 1);
        return last <= 0 ? text[..maxChars] : text[..last];
    }
}
