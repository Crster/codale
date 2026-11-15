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

    /// <summary>The marker that stands for the caret when code goes to a chat (instruct) model.</summary>
    public const string CursorMarker = "<CURSOR>";

    /// <summary>
    /// Lines kept from a proposal on an empty line: one statement, a small bit to Tab through rather than a block to
    /// read and review (the next pause asks for the line after it).
    /// </summary>
    public const int MaxBlockLines = 1;

    /// <summary>
    /// The code around the caret as one file with <see cref="CursorMarker"/> in its place, for an instruct model.
    /// Raw text, not JSON: a JSON string escapes quotes and brackets (<c>"</c>), and models copy the escapes back.
    /// </summary>
    public static string CursorDocument(string? fileName, string prefix, string suffix) =>
        $"File: {(string.IsNullOrWhiteSpace(fileName) ? "(unsaved)" : fileName.Replace('\\', '/'))}\n<code>\n{prefix}{CursorMarker}{suffix}\n</code>";

    /// <summary>
    /// The ghost text for an instruct model's raw insertion at the caret. With code before the caret on its line, only
    /// that line is finished; on an empty line a short block is kept, up to where it starts repeating the code that
    /// follows. Indentation the caret already has, a restatement of the caret's line and a repeat of the rest of the line
    /// are dropped. Empty when nothing is left.
    /// </summary>
    public static string Tidy(string raw, string prefix, string suffix)
    {
        var text = raw.Replace("\r\n", "\n").Replace(CursorMarker, "", StringComparison.Ordinal);
        var caretLine = prefix.Replace("\r\n", "\n");
        caretLine = caretLine[(caretLine.LastIndexOf('\n') + 1)..];
        var midLine = caretLine.Trim().Length > 0;

        if (midLine)
        {
            // A reply that starts on a new line says the caret's line is finished: its next line does not belong here.
            if (text.TrimStart(' ', '\t').StartsWith('\n'))
            {
                return "";
            }

            text = FirstLine(text, suffix, prefix);
            // The caret's own indentation copied into the middle of a line ("(" + "        cards[j]").
            if (text.Length > 1 && char.IsWhiteSpace(text[0]) && char.IsWhiteSpace(text[1]))
            {
                text = text.TrimStart(' ', '\t');
            }

            return WithoutRestatedLine(caretLine, text);
        }

        var lines = text.TrimStart('\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        var next = suffix.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var kept = new List<string>();
        foreach (var line in lines)
        {
            // The model went on to write the code that already follows the caret.
            if (kept.Count >= MaxBlockLines || (next is { Length: > 1 } && line.Trim() == next))
            {
                break;
            }

            kept.Add(line);
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count == 0)
        {
            return "";
        }

        // The caret already sits on the indent: the first line's own indent would double it.
        if (caretLine.Length > 0)
        {
            kept[0] = kept[0].TrimStart(' ', '\t');
        }

        var block = string.Join("\n", kept);
        return block.Trim().Length == 0 ? "" : block;
    }

    /// <summary>
    /// Drops the start of <paramref name="completion"/> when it restates the end of the caret's line
    /// ("var j = " + "j = random.Next(i + 1);"): the longest overlap that is at least three characters and starts a token.
    /// </summary>
    private static string WithoutRestatedLine(string caretLine, string completion)
    {
        var line = caretLine.TrimStart();
        for (var length = Math.Min(line.Length, completion.Length); length >= 3; length--)
        {
            var start = line.Length - length;
            var startsToken = start == 0 || !IsWord(line[start - 1]) || !IsWord(line[start]);
            if (startsToken && string.CompareOrdinal(line, start, completion, 0, length) == 0)
            {
                var rest = completion[length..];
                return rest.Trim().Length == 0 ? "" : rest;
            }
        }

        return completion;

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
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