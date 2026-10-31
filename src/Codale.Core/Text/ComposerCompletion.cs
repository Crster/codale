namespace Codale.Core.Text;

/// <summary>Locates and edits the completion token at the composer's caret.</summary>
public static class ComposerCompletion
{
    public readonly record struct Token(int Start, int End, char Trigger, string Query);
    public readonly record struct Edit(string Text, int Caret);

    public static Token? FindToken(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start == caret || text[start] is not ('/' or '@')) return null;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return new Token(start, end, text[start], text[(start + 1)..caret]);
    }

    public static Edit InsertTrigger(string text, int caret, int selectionLength, char trigger)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - caret);
        if (trigger == '/')
        {
            // Keep the prompt intact while the command query is edited before it.
            return new Edit("/ " + text, 1);
        }
        var prefix = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " @" : "@";
        var suffix = caret + selectionLength < text.Length && !char.IsWhiteSpace(text[caret + selectionLength]) ? " " : "";
        return new Edit(text[..caret] + prefix + suffix + text[(caret + selectionLength)..], caret + prefix.Length);
    }

    public static Edit Apply(string text, int caret, string insertion, bool isCommand)
    {
        var token = FindToken(text, caret);
        if (token is not { } active || active.Trigger != (isCommand ? '/' : '@')) return new Edit(text, caret);
        var before = text[..active.Start];
        var after = text[active.End..];
        if (isCommand)
        {
            var prompt = (before + after).Trim();
            var command = insertion + " ";
            return new Edit(command + prompt, command.Length + prompt.Length);
        }
        var reference = "@" + (insertion.Any(char.IsWhiteSpace) ? $"\"{insertion}\"" : insertion);
        var separator = after.Length == 0 || !char.IsWhiteSpace(after[0]) ? " " : "";
        return new Edit(before + reference + separator + after, before.Length + reference.Length + (separator.Length > 0 ? separator.Length : 1));
    }
}
