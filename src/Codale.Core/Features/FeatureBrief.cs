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
        "Write the briefing the agent reads before it starts: only what bears on the request, so it can begin " +
        "without re-reading the history or searching the codebase from scratch.\n" +
        "\n" +
        "Output: Markdown with these headings, in this order, leaving out any with nothing under it:\n" +
        "## Relevant background\n## Decisions & constraints\n## Where to look\n## Suggested first steps\n## Open questions\n" +
        "\n" +
        "Rules:\n" +
        "- Be concrete: exact file paths with line numbers (path:line), names, commands, settings and error messages.\n" +
        "- Under Where to look, list the few places the request will touch and why, most important first.\n" +
        "- Say what earlier work finished and verified, and what was left planned or broken, when it matters here.\n" +
        "- Keep the user's stated preferences and constraints for the feature.\n" +
        "- The progress may hold several conversations, one block each: keep each reply with its own request. Work in a " +
        "conversation marked still running or never finished is in progress, not done. When <other_chats> lists a chat " +
        "working now, name what it is doing under Open questions and steer clear of its files unless the request needs them.\n" +
        "- Include a fact or file only if it directly helps with <request>; omit unrelated feature history, even recent work. " +
        "If nothing is relevant, say so briefly. Do not reproduce file contents, code snippets, command output, or tool output.\n" +
        "- At most 40 lines.\n" +
        "- Brief; never carry out, answer or plan beyond first steps for the request.\n" +
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
