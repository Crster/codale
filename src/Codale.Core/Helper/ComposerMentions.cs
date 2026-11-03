namespace Codale.Core.Helper;

/// <summary>
/// Places "@file" mentions in the composer as words of their own. Pasted against text
/// ("rites.@b7a3ce.png", "@a.png@b.png") a mention fuses with its neighbours, and the CLI
/// reads one unknown path instead of the attachment.
/// </summary>
public static class ComposerMentions
{
    /// <summary>
    /// <paramref name="text"/> with <paramref name="mentions"/> replacing the selection at
    /// <paramref name="start"/>, with a space before them unless they start the text or follow
    /// whitespace, and a space after unless whitespace follows. Returns the caret, after the mentions
    /// and their trailing space, so typing carries on as a new word.
    /// </summary>
    public static (string Text, int Caret) Insert(string text, int start, int length, string mentions)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        var before = text[..start];
        var after = text[(start + length)..];
        mentions = mentions.Trim();

        var inserted = (before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "") + mentions +
            (after.Length > 0 && char.IsWhiteSpace(after[0]) ? "" : " ");
        // After the separating whitespace that follows, so the caret is ready for the next word.
        var caret = before.Length + inserted.Length + (inserted.EndsWith(' ') ? 0 : 1);
        return (before + inserted + after, Math.Min(caret, before.Length + inserted.Length + after.Length));
    }
}
