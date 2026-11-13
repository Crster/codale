namespace Codale.Editor;

/// <summary>Plain-text (ordinal) search over a buffer, line by line; needles never span lines.</summary>
public static class TextSearch
{
    /// <summary>Every match, top to bottom, at most <paramref name="max"/>.</summary>
    public static IEnumerable<DocumentPosition> FindAll(TextBuffer buffer, string needle, bool wholeWord, int max = int.MaxValue,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (needle.Length == 0 || needle.Contains('\n'))
        {
            yield break;
        }

        var found = 0;
        for (var line = 0; line < buffer.LineCount && found < max; line++)
        {
            var text = buffer[line];
            if (text.Length < needle.Length)
            {
                continue;
            }

            for (var at = text.IndexOf(needle, comparison); at >= 0 && found < max; at = text.IndexOf(needle, at + needle.Length, comparison))
            {
                if (!wholeWord || IsWholeWordAt(text, at, needle.Length))
                {
                    found++;
                    yield return new DocumentPosition(line, at);
                }
            }
        }
    }

    /// <summary>Matches from <paramref name="from"/> to the end, then from the top back to it.</summary>
    public static IEnumerable<DocumentPosition> FindFrom(TextBuffer buffer, string needle, DocumentPosition from, bool wholeWord)
    {
        if (needle.Length == 0 || needle.Contains('\n'))
        {
            yield break;
        }

        var lines = buffer.LineCount;
        for (var step = 0; step <= lines; step++)
        {
            var line = (from.Line + step) % lines;
            var text = buffer[line];
            var startCol = step == 0 ? Math.Min(from.Col, text.Length) : 0;
            for (var at = text.IndexOf(needle, startCol, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            {
                // The last lap revisits the starting line only up to where the search began.
                if (step == lines && at >= from.Col)
                {
                    break;
                }

                if (!wholeWord || IsWholeWordAt(text, at, needle.Length))
                {
                    yield return new DocumentPosition(line, at);
                }
            }
        }
    }

    public static bool IsWholeWordAt(string text, int at, int length) =>
        (at == 0 || !TextUnits.IsWordChar(text[at - 1]))
        && (at + length >= text.Length || !TextUnits.IsWordChar(text[at + length]));
}
