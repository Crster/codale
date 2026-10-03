namespace Codale.Core.Text;

/// <summary>Truncation that never splits a UTF-16 surrogate pair.</summary>
public static class TextClip
{
    /// <summary>Returns at most <paramref name="max"/> chars of <paramref name="text"/>, without a trailing lone high surrogate.</summary>
    public static string Truncate(string text, int max)
    {
        if (max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        var end = max;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}
