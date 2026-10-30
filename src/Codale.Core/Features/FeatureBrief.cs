using System.Text;

using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Core.Features;

/// <summary>A chat open on a feature right now: its tab title, whether a turn is in flight, and what it last asked.</summary>
public sealed record FeatureChat(string ChatId, string FeatureId, string Title, bool IsBusy, string? LastRequest);

/// <summary>A file the pre-exploration found for a request: where to look, and why.</summary>
public sealed record ExploredFile(string Path, IReadOnlyList<(int Start, int End)> Ranges, string Reason = "", string Snippet = "");

/// <summary>
/// The handoff a fresh session starts a feature from when the background-task model is around.
/// Instead of the summary plus every raw turn since it (which the chat model would pay for on
/// every turn of the session), the cheap model reads all of that together with the new request
/// and the code a pre-exploration found, and writes a short brief aimed at the request.
/// Prompt and budgets live here so they are testable; the calls are the app's.
/// </summary>
public static class FeatureBrief
{
    /// <summary>What the explored code may take up in the model's input.</summary>
    public const int ExploredInputChars = 12_000;

    /// <summary>What the seed the chat model receives should stay under, brief included.</summary>
    public const int SeedBudgetChars = 6_000;

    /// <summary>The fallback's progress cap: the raw log, trimmed, when no brief could be written.</summary>
    public const int FallbackProgressChars = 24_000;

    private const int MaxSeedFiles = 12;
    private const int ReplyTokens = 1500;

    public const string SystemPrompt =
        "You prepare a coding agent to act on the request in <request> in a fresh session that has none of the " +
        "earlier conversations. You get the feature's name, its summary, the progress since that summary, the files " +
        "written for it, and the code a search found for the request.\n" +
        "\n" +
        "Generate a very short handoff focused on the new user request. Before writing it:\n" +
        "1. Read <request> to identify the feature behavior or change the user means.\n" +
        "2. Scan the summary and previous history for that behavior. Within each conversation, align each user " +
        "request with the agent responses that follow it, up to the next user request. Never attach a response " +
        "to another request or conversation.\n" +
        "3. Collect a small working cache of relevant facts from those exchanges: what the feature does, what " +
        "was actually completed, applicable constraints, and exact file locations. Reconcile it with the summary " +
        "and code search results; newer evidence supersedes older claims. Use this cache to write the handoff, " +
        "but do not output the cache or your reasoning.\n" +
        "\n" +
        "Output: Markdown with exactly these three headings, in this order:\n" +
        "## What it is\nOne short sentence describing the feature behavior relevant to <request>.\n" +
        "## Done\nOne or two short bullets stating what earlier work completed for <request>, with verification " +
        "only when recorded. Include a remaining issue or constraint only if it affects this request.\n" +
        "## Where to look\nA narrow list of 1-4 exact files to start reading or editing, most useful first.\n" +
        "\n" +
        "Rules:\n" +
        "- Keep the current file-list format: - `path:line` - brief reason to start there. Preserve supplied line " +
        "ranges as `path:start-end` when useful; if no line is known, use only the exact path.\n" +
        "- List only file paths and line numbers present in the input. Prefer the implementation entry point " +
        "and directly related edit locations; omit broad directories, file inventories and speculative files. " +
        "If no exact file is supported, say 'No exact file identified.' instead of guessing.\n" +
        "- Distinguish completed work from requests, proposals and unfinished work; do not treat a request as " +
        "evidence it was implemented. Keep only user preferences and constraints that affect <request>.\n" +
        "- The progress may hold several conversations, one block each: keep each reply with its own request. Work in a " +
        "conversation marked still running or never finished is in progress, not done. When <other_chats> lists a chat " +
        "working now on this request, briefly mark that work as in progress under Done and flag overlapping files.\n" +
        "- Include a fact or file only if it directly helps with <request>; omit unrelated feature history, even recent work. " +
        "If no relevant completed work is recorded, say so under Done. Do not reproduce file contents, code snippets, " +
        "command output, tool output or the conversation.\n" +
        "- At most 180 words and 18 lines total. No extra sections, preamble, action plan or open-question list.\n" +
        "- Summarise the relevant context; never carry out or answer the request.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.ResultOnly;

    /// <summary>The model's input: everything known about the feature, fenced as data, with the request last.</summary>
    public static string BuildInput(
        string featureName,
        string summary,
        IReadOnlyList<HandoffEntry> progress,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<ExploredFile> explored,
        string request,
        IReadOnlyList<FeatureChat>? otherChats = null)
    {
        otherChats ??= [];
        var running = RunningIds(otherChats);
        var text = new StringBuilder();
        text.AppendLine(PromptRules.Tag("feature", featureName));
        text.AppendLine(PromptRules.Tag("summary", summary.Length == 0 ? "(none yet)" : summary.Trim()));
        var conversation = progress.Where(e => e.Role != HandoffRole.Tool).ToList();
        text.AppendLine(PromptRules.Tag("progress_since_summary", conversation.Count == 0 ? "(none)" : SessionHandoff.Condense(conversation, runningChats: running)));
        if (writtenFiles.Count > 0)
        {
            text.AppendLine(PromptRules.Tag("files_written", string.Join('\n', writtenFiles)));
        }

        if (explored.Count > 0)
        {
            text.AppendLine(PromptRules.Tag("code_found", DescribeExplored(explored, ExploredInputChars)));
        }

        if (otherChats.Count > 0)
        {
            text.AppendLine(PromptRules.Tag("other_chats", DescribeChats(otherChats)));
        }

        text.Append(PromptRules.Tag("request", request.Trim()));
        return text.ToString();
    }

    /// <summary>The chats among <paramref name="chats"/> with a turn in flight, for <see cref="SessionHandoff.Condense"/>.</summary>
    public static IReadOnlySet<string> RunningIds(IEnumerable<FeatureChat> chats) =>
        chats.Where(c => c.IsBusy).Select(c => c.ChatId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The seed's note on the other chats open on the feature, written by the app rather than the
    /// model so it is always there: two agents on one feature should not undo each other's edits.
    /// </summary>
    public static string OtherChatsSection(IReadOnlyList<FeatureChat> otherChats) =>
        otherChats.Count == 0
            ? ""
            : "## Other chats on this feature right now\n" +
              "These are open beside this session and may be changing the same files. Do not redo or undo their work; " +
              "if the request overlaps it, say so.\n" + DescribeChats(otherChats);

    private static string DescribeChats(IReadOnlyList<FeatureChat> chats) =>
        string.Join('\n', chats.Take(6).Select(c =>
            $"- \"{c.Title}\" ({(c.IsBusy ? "working now" : "idle")})" +
            (c.LastRequest is { Length: > 0 } asked ? $": last asked \"{Clip(asked.ReplaceLineEndings(" ").Trim(), 200)}\"" : "")));

    /// <summary>What the chat model receives: the feature, the brief, and where the pre-exploration looked.</summary>
    public static string BuildSeed(string featureName, string brief, IReadOnlyList<ExploredFile> explored, IReadOnlyList<FeatureChat>? otherChats = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Feature: {featureName}");
        text.AppendLine();
        text.AppendLine("A background pass already read this feature's history and searched the code for the request below; " +
                        "this is its briefing. Check the files before relying on it.");
        text.AppendLine();
        text.AppendLine(Clip(brief.Trim(), SeedBudgetChars - 1_500));

        if (OtherChatsSection(otherChats ?? []) is { Length: > 0 } others)
        {
            text.AppendLine();
            text.AppendLine(others);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The file list the fallback seed gains when the exploration finished but the brief did not.</summary>
    public static string ExploredSection(IReadOnlyList<ExploredFile> explored) =>
        explored.Count == 0 ? "" : "## Files a search found for the request\n" + DescribeExplored(explored, 1_200).TrimEnd();

    /// <summary>The brief for a request.</summary>
    public static async Task<string> WriteAsync(
        IHelperModel model,
        string featureName,
        string summary,
        IReadOnlyList<HandoffEntry> progress,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<ExploredFile> explored,
        string request,
        IReadOnlyList<FeatureChat>? otherChats = null,
        CancellationToken ct = default) =>
        ModelOutput.CleanText(await model
            .CompleteAsync(SystemPrompt, BuildInput(featureName, summary, progress, writtenFiles, explored, request, otherChats), ReplyTokens, ct)
            .ConfigureAwait(false));

    /// <summary>One line per file (path:start-end and why), optionally each with its snippet, within <paramref name="budget"/>.</summary>
    private static string DescribeExplored(IReadOnlyList<ExploredFile> explored, int budget)
    {
        var text = new StringBuilder();
        foreach (var file in explored.Take(MaxSeedFiles))
        {
            var ranges = file.Ranges.Count == 0
                ? ""
                : ":" + string.Join(",", file.Ranges.Take(3).Select(r => r.End > r.Start ? $"{r.Start}-{r.End}" : $"{r.Start}"));
            var line = $"- `{file.Path}{ranges}`" + (file.Reason.Length > 0 ? $" - {file.Reason}" : "") + "\n";
            var block = line;

            if (text.Length + block.Length > budget)
            {
                // A snippet that does not fit still leaves room for the file's line.
                if (text.Length + line.Length > budget)
                {
                    break;
                }

                block = line;
            }

            text.Append(block);
        }

        return text.ToString();
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "\n[...]";
}
