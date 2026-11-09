namespace Codale.Core.Background;

/// <summary>
/// True fill-in-the-middle completion: the model sees the text before the caret and the text after it,
/// and writes what goes between. Used for editor ghost text and shell autocomplete.
/// </summary>
public interface IFimCompleter
{
    /// <summary>
    /// One line to insert at the caret, or empty when the model has nothing to add. Cancel it when the
    /// user types again.
    /// </summary>
    /// <param name="rejected">Proposals the user already passed over at this caret; the answer differs from each.</param>
    /// <exception cref="Helper.HelperModelException">The server could not be reached or refused the request.</exception>
    Task<string> CompleteAsync(string prefix, string suffix, IReadOnlyList<string>? rejected, CancellationToken ct = default);
}

/// <summary>Turns a fill-in-the-middle model's raw output into the one line the caret gets.</summary>
public static class FimText
{
    private static readonly System.Buffers.SearchValues<char> Punctuation =
        System.Buffers.SearchValues.Create(")]}>;,.:'\"` ");

    /// <summary>
    /// The first line of <paramref name="raw"/>, without any part of it that is already written right after
    /// the caret: a model often re-emits the closing <c>);</c> it was shown in the suffix.
    /// </summary>
    public static string FirstLine(string raw, string suffix)
    {
        var text = raw.Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        if (newline >= 0)
        {
            text = text[..newline];
        }

        text = text.TrimEnd();
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
    /// True when <paramref name="completion"/> reads as a chat reply rather than shell text: a model that
    /// is not a code model answers "run" with "It looks like you're in...". Such text is never a command.
    /// </summary>
    public static bool LooksLikeProse(string completion)
    {
        var text = completion.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        string[] markers = ["it looks like", "you're", "you are", "i'll", "i can", "here's", "here is", "let me", "sure,", "this is", "i notice", "based on"];
        if (markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 6 && (text.EndsWith(':') || text.Contains(". ", StringComparison.Ordinal) || text.EndsWith('.'));
    }

    /// <summary>
    /// The text before the caret for shell autocomplete, written as the start of a script a code model
    /// knows how to continue: the context as comments, the recent commands as earlier lines, then what is typed.
    /// </summary>
    public static string ShellPrefix(string shell, string cwd, IEnumerable<string> recent, IEnumerable<string> files, string typed)
    {
        var isCmd = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        var isPowerShell = shell.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            shell.Contains("powershell", StringComparison.OrdinalIgnoreCase);
        var comment = isCmd ? "::" : "#";
        var name = isCmd ? "Windows Command Prompt (cmd.exe)" : isPowerShell ? "Windows PowerShell" : shell;
        var prompt = isCmd ? $"{cwd}>" : isPowerShell ? $"PS {cwd}>" : $"{cwd}$";

        // A terminal session transcript: the model continues the last prompt line with one shell command.
        var builder = new System.Text.StringBuilder();
        builder.Append(comment).Append(" Transcript of an interactive ").Append(name).Append(" session on Windows.\n");
        builder.Append(comment).Append(" Every line after a prompt is one real command typed by the user, never prose.\n");
        if (string.Join(", ", files) is { Length: > 0 } names)
        {
            builder.Append(comment).Append(" Files in the current folder: ").Append(Clip(names, 1200, fromEnd: false)).Append('\n');
        }

        foreach (var command in recent.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            builder.Append(prompt).Append(' ').Append(command.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }

        return builder.Append(prompt).Append(' ').Append(typed).ToString();
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
