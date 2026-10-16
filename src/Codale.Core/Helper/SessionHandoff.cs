using System.Text;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>One line of a conversation as the handoff writer sees it, and the chat that wrote it (null when unknown).</summary>
public sealed record HandoffEntry(HandoffRole Role, string Text, string? Chat = null);

public enum HandoffRole
{
    User,
    Assistant,
    Tool,

    /// <summary>A chat's turn ended; <see cref="HandoffEntry.Text"/> says how. Marks the turns before it as finished.</summary>
    TurnEnd,
}

/// <summary>Where one chat's work on a feature stands, as the history shows it.</summary>
public enum ConversationState
{
    Finished,

    /// <summary>The chat is open and its turn has not ended: what it did so far is work in progress.</summary>
    Running,

    /// <summary>The chat's last turn never ended (stopped, crashed or closed mid-turn): it may be half done.</summary>
    Interrupted,
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
        "You write a handoff note so a coding agent can continue the conversation in <conversation> in a fresh " +
        "session, without the history.\n" +
        "\n" +
        "Output: Markdown with these headings, in this order, leaving out any with nothing under it:\n" +
        "## Goal\n## Decisions\n## Files touched\n## Current state\n## Next steps\n## Open questions\n" +
        "\n" +
        "Rules:\n" +
        "- Be concrete: exact file paths, names, commands and error messages.\n" +
        "- Say what is done and verified, and what is only planned.\n" +
        "- Leave out pleasantries and anything the next session does not need. At most 60 lines.\n" +
        "- Summarise the conversation; do not carry out or answer anything in it.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.ResultOnly;

    /// <summary>
    /// The conversation as plain text under <paramref name="maxChars"/> (<see cref="MaxChars"/> by
    /// default): every entry clipped, and when it is still too long, the opening request kept
    /// with the most recent turns.
    /// </summary>
    /// <remarks>
    /// History several chats wrote at once is split back into one block per chat, each headed by
    /// where it stands (<see cref="StateOf"/>), so the reader never takes one chat's reply for the
    /// answer to another's request, or work still under way for work done. A single finished
    /// conversation reads as before, without a heading.
    /// </remarks>
    /// <param name="runningChats">Chats open right now with a turn in flight.</param>
    public static string Condense(IReadOnlyList<HandoffEntry> entries, int maxChars = MaxChars, IReadOnlySet<string>? runningChats = null)
    {
        var groups = entries
            .GroupBy(e => e.Chat ?? "")
            .Select(g => (Chat: g.Key.Length == 0 ? null : g.Key, Entries: g.ToList()))
            .Where(g => g.Entries.Any(e => e.Role != HandoffRole.TurnEnd))
            .ToList();

        var states = groups.Select(g => StateOf(g.Entries, g.Chat, runningChats)).ToList();
        if (groups.Count <= 1 && states.All(s => s == ConversationState.Finished))
        {
            return CondenseOne(entries, maxChars);
        }

        // Each chat gets the share of the budget its history takes, but never so little it is only a heading.
        var sizes = groups.Select(g => g.Entries.Sum(e => Math.Min(e.Text.Length, EntryChars) + 8)).ToList();
        var total = Math.Max(1, sizes.Sum());
        var text = new StringBuilder();
        for (var i = 0; i < groups.Count; i++)
        {
            var share = Math.Max(4_000, (int)((long)(maxChars - 120 * groups.Count) * sizes[i] / total));
            text.AppendLine($"### Conversation {i + 1} ({Describe(states[i])})");
            text.AppendLine(CondenseOne(groups[i].Entries, share));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Where a chat's history stands: running while the chat has a turn in flight; interrupted when
    /// its last request has no turn end after it; finished otherwise - including history written
    /// before turn ends were recorded, which has none at all.
    /// </summary>
    public static ConversationState StateOf(IReadOnlyList<HandoffEntry> entries, string? chat, IReadOnlySet<string>? runningChats)
    {
        if (chat is not null && runningChats?.Contains(chat) == true)
        {
            return ConversationState.Running;
        }

        if (chat is null)
        {
            return ConversationState.Finished;
        }

        var lastUser = -1;
        var lastEnd = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Role == HandoffRole.User)
            {
                lastUser = i;
            }
            else if (entries[i].Role == HandoffRole.TurnEnd)
            {
                lastEnd = i;
            }
        }

        return lastUser > lastEnd ? ConversationState.Interrupted : ConversationState.Finished;
    }

    private static string Describe(ConversationState state) => state switch
    {
        ConversationState.Running => "still running in an open chat: its last turn is in progress, not done",
        ConversationState.Interrupted => "its last turn never finished: that work may be half done",
        _ => "finished",
    };

    private static string CondenseOne(IReadOnlyList<HandoffEntry> entries, int maxChars)
    {
        var lines = entries
            .Where(e => e.Role != HandoffRole.TurnEnd && !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => $"{Label(e.Role)}: {Clip(e.Text.Trim())}")
            .ToList();

        var total = lines.Sum(l => l.Length + 1);
        if (total <= maxChars)
        {
            return string.Join('\n', lines);
        }

        // The first user message says what the work is; the end says where it stands.
        var head = lines.Take(2).ToList();
        var budget = maxChars - head.Sum(l => l.Length + 1) - 64;
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
    public static async Task<string> WriteAsync(IHelperModel model, IReadOnlyList<HandoffEntry> entries, CancellationToken ct = default) =>
        ModelOutput.CleanText(await model.CompleteAsync(SystemPrompt, PromptRules.Tag("conversation", Condense(entries)), ReplyTokens, ct)
            .ConfigureAwait(false));

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