using System.Text;

namespace Codale.Core.Helper;

/// <summary>One line of a conversation as the handoff writer sees it.</summary>
public sealed record HandoffEntry(HandoffRole Role, string Text);

public enum HandoffRole
{
    User,
    Assistant,
    Tool,
}

/// <summary>
/// A long conversation carried into a fresh session as a summary written by the
/// background-task model. A fresh session stops re-sending the whole history on every
/// turn, and unlike <c>/compact</c> the summary costs the chat model nothing.
/// </summary>
public static class SessionHandoff
{
    /// <summary>What the writer is shown at most; the first request and the latest turns are what matter.</summary>
    public const int MaxChars = 120_000;

    private const int EntryChars = 2_000;
    private const int ReplyTokens = 2048;

    public const string SystemPrompt =
        "You write a handoff note so a coding agent can continue this conversation in a fresh session " +
        "without the history. Use these markdown headings, in order: Goal, Decisions, Files touched, " +
        "Current state, Next steps, Open questions. Be concrete: exact file paths, names, commands and " +
        "error messages; what is done and verified versus what is only planned. Leave out pleasantries " +
        "and anything the next session does not need. At most 60 lines.";

    /// <summary>
    /// The conversation as plain text under <see cref="MaxChars"/>: every entry clipped, and
    /// when it is still too long, the opening request kept with the most recent turns.
    /// </summary>
    public static string Condense(IReadOnlyList<HandoffEntry> entries)
    {
        var lines = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => $"{Label(e.Role)}: {Clip(e.Text.Trim())}")
            .ToList();

        var total = lines.Sum(l => l.Length + 1);
        if (total <= MaxChars)
        {
            return string.Join('\n', lines);
        }

        // The first user message says what the work is; the end says where it stands.
        var head = lines.Take(2).ToList();
        var budget = MaxChars - head.Sum(l => l.Length + 1) - 64;
        var tail = new List<string>();
        for (var i = lines.Count - 1; i >= head.Count && budget > 0; i--)
        {
            budget -= lines[i].Length + 1;
            if (budget >= 0)
            {
                tail.Insert(0, lines[i]);
            }
        }

        var omitted = lines.Count - head.Count - tail.Count;
        return string.Join('\n', [.. head, $"[... {omitted} earlier entries omitted ...]", .. tail]);
    }

    /// <summary>The handoff note for a conversation.</summary>
    public static Task<string> WriteAsync(IHelperModel model, IReadOnlyList<HandoffEntry> entries, CancellationToken ct = default) =>
        model.CompleteAsync(SystemPrompt, "Conversation so far:\n\n" + Condense(entries), ReplyTokens, ct);

    /// <summary>The first message of the fresh session, ready for the user to add to and send.</summary>
    public static string FirstMessage(string note) =>
        new StringBuilder()
            .AppendLine("Continue from this handoff of an earlier session. Check the current state in the files before you change anything.")
            .AppendLine()
            .AppendLine(note.Trim())
            .ToString()
            .TrimEnd();

    private static string Label(HandoffRole role) => role switch
    {
        HandoffRole.User => "User",
        HandoffRole.Assistant => "Agent",
        _ => "Tool",
    };

    private static string Clip(string text) =>
        text.Length <= EntryChars ? text : text[..(EntryChars / 2)] + " [...] " + text[^(EntryChars / 2)..];
}
