namespace Codale.Core.Text;

/// <summary>
/// The word the composer's prediction works on: the one the caret ends, in prose. Words inside an
/// @mention, a /command, a path or a link are left alone - those have their own completion.
/// </summary>
public static class PromptWords
{
    public readonly record struct Word(int Start, int Length, string Text)
    {
        public int End => Start + Length;
    }

    /// <summary>The word that ends exactly at <paramref name="caret"/>, when the caret is at its end and it is plain prose.</summary>
    public static Word? Before(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length || !IsWordChar(text[caret - 1]))
        {
            return null;
        }

        if (caret < text.Length && IsWordChar(text[caret]))
        {
            return null;
        }

        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        if (char.IsDigit(text[start]) || !IsProse(text, start, caret))
        {
            return null;
        }

        return new Word(start, caret - start, text[start..caret]);
    }

    /// <summary>The word before the one starting at <paramref name="start"/>, across spaces and dots, for ranking what follows it.</summary>
    public static string? Previous(string text, int start)
    {
        var end = start;
        while (end > 0 && (text[end - 1] is ' ' or '.' or '\t'))
        {
            end--;
        }

        var begin = end;
        while (begin > 0 && IsWordChar(text[begin - 1]))
        {
            begin--;
        }

        return begin < end ? text[begin..end] : null;
    }

    /// <summary>
    /// <paramref name="suggestion"/> in the casing the user started typing: "Comp" makes "compile" read
    /// "Compile", "COMP" makes it "COMPILE". A suggestion that has capitals of its own is a name and kept.
    /// </summary>
    public static string MatchCase(string typed, string suggestion)
    {
        if (typed.Length == 0 || suggestion.Length == 0 || suggestion.Any(char.IsUpper))
        {
            return suggestion;
        }

        if (typed.Length > 1 && typed.All(c => !char.IsLetter(c) || char.IsUpper(c)) && typed.Any(char.IsLetter))
        {
            return suggestion.ToUpperInvariant();
        }

        return char.IsUpper(typed[0]) ? char.ToUpperInvariant(suggestion[0]) + suggestion[1..] : suggestion;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>False when the whitespace-separated token holding the word is a mention, a command, a path or a link.</summary>
    private static bool IsProse(string text, int start, int end)
    {
        var tokenStart = start;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        if (text[tokenStart] is '@' or '/' or '\\' or '#' or '$' or '-')
        {
            return false;
        }

        var token = text.AsSpan(tokenStart, end - tokenStart);
        return token.IndexOfAny('/', '\\', '@') < 0 && !token.Contains("://", StringComparison.Ordinal);
    }
}
