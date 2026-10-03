using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Core.Agents;

/// <summary>
/// A short session name from the opening prompt, for when no model names it: the
/// request itself, without the politeness around it, cut at the first sentence.
/// </summary>
public static partial class SessionTitles
{
    private const int MaxWords = 6;
    private const int MaxChars = 36;

    /// <summary>"/clear" (or "/reset", "/new") wipes the conversation, so it says nothing about what the session is for.</summary>
    public static bool IsResetCommand(string? text) =>
        text?.Trim() is "/clear" or "/reset" or "/new";

    public static string FromPrompt(string prompt)
    {
        var text = CodePattern().Replace(prompt, " ");
        text = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";

        text = StripFillers(text);

        // The first sentence or clause carries the request.
        var end = text.IndexOfAny(['.', '?', '!', ';', ',']);
        if (end > 0)
        {
            text = text[..end];
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var title = "";
        foreach (var word in words.Take(MaxWords))
        {
            var next = title.Length == 0 ? word : $"{title} {word}";
            if (next.Length > MaxChars)
            {
                break;
            }

            title = next;
        }

        if (title.Length == 0)
        {
            // One huge word, or nothing but filler: the prompt clipped as it is.
            var single = prompt.ReplaceLineEndings(" ").Trim();
            return single.Length > MaxChars ? TextClip.Truncate(single, MaxChars).TrimEnd() + "…" : single;
        }

        title = title.TrimEnd(':', '-', ' ');
        return char.ToUpperInvariant(title[0]) + title[1..];
    }

    /// <summary>
    /// A model-written title made safe to show: chat-template tokens that leak into a small
    /// model's reply ("&lt;|im_end|&gt;", "&lt;/s&gt;", mangled variants like "&lt;im|end&gt;") are
    /// dropped along with anything after them, then the usual wrapping quotes and stops.
    /// </summary>
    public static string CleanModelTitle(string raw)
    {
        var text = raw;
        var cut = text.IndexOf("<|", StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        text = SpecialTokenPattern().Replace(text, " ");
        text = WhitespacePattern().Replace(text, " ");
        return text.Trim().Trim('"', '\'', '.', '!', '*', '|', '<', '>').Trim();
    }

    /// <summary>"can you please help me fix x" -> "fix x": strips leading fillers until none is left.</summary>
    internal static string StripFillers(string text)
    {
        text = text.TrimStart();
        string stripped;
        do
        {
            stripped = text;
            text = FillerPattern().Replace(text, "").TrimStart(' ', ',', ':', '-');
        }
        while (text != stripped);

        return text;
    }

    /// <summary>A short tag made of letters, underscores, slashes and pipes in angle brackets: never part of a real title.</summary>
    [GeneratedRegex(@"</?\|?[A-Za-z_|/]{1,16}\|?>")]
    private static partial Regex SpecialTokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"```[\s\S]*?(```|$)")]
    private static partial Regex CodePattern();

    [GeneratedRegex(
        @"^(hey|hi|hello|ok(ay)?|so|please|pls|plz|kindly|can you|could you|would you|will you|can we|could we|" +
        @"i want you to|i want to|i need you to|i need to|i'd like you to|i'd like to|i would like to|" +
        @"help me( to)?|let's|lets|we need to|we should|you should|make sure to|try to|go ahead and|" +
        @"just|now|also|then)\b\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex FillerPattern();
}
