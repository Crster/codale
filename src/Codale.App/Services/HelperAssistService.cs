using System.Text;

using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Tasks;
using Codale.Core.Text;
using Codale.Search;

namespace Codale.App.Services;

/// <summary>One explore in flight: its steps as they happen, and when it ends.</summary>
public sealed class ExploreRun(string question)
{
    public string Question { get; } = question;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Raised from the search's thread for each step.</summary>
    public event Action<string>? Progress;

    /// <summary>Raised once, with true when the search failed.</summary>
    public event Action<bool>? Finished;

    private readonly CancellationTokenSource _cancel = new();

    internal CancellationToken Token => _cancel.Token;

    /// <summary>Stops the search; the tool call then fails with a cancellation.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    internal void Report(string line) => Progress?.Invoke(line);

    internal void Finish(bool failed) => Finished?.Invoke(failed);
}

/// <summary>
/// The app's end of <see cref="ITaskAssist"/>: explore and output digests for a
/// Claude session, answered by the background-task model so the chat model never reads
/// what they read. Works only on a BYOK provider - the Claude CLI fallback would spend the
/// same tokens it is meant to save - and says so instead of answering.
/// </summary>
public sealed class HelperAssistService(IHelperModel helper, string runDirectory, Func<bool> hasApi) : ITaskAssist
{
    /// <summary>
    /// The search's share of the client's wait (<see cref="TaskPipeClient.ExploreTimeout"/>):
    /// the rest is kept for the closing answer and the write-up, which run after it.
    /// </summary>
    private static readonly TimeSpan ExploreBudget = TaskPipeClient.ExploreTimeout - TimeSpan.FromMinutes(3);

    /// <summary>Model rounds may each make several calls; a wide cap lets a hard question finish.</summary>
    private const int ExploreSteps = 60;

    private const int DigestInputChars = 150_000;
    private const int LongReplyTokens = 2048;

    private const string Unavailable =
        "Codale's background-task model is not a BYOK provider, so this tool is off. Use Grep/Glob/Read instead.";

    private const string DigestPrompt =
        "You condense a shell command's output for a coding agent that must act on it.\n" +
        "Input: the command, and its output inside <output>.\n" +
        "\n" +
        "Keep, verbatim and in their original order: every error, warning, failed test, exception and stack frame " +
        "that names project code, with its file path and line number; counts, totals, exit status and the final result line.\n" +
        "Drop: progress, download, restore and passing-test noise, and repeated lines.\n" +
        "Output: plain text, at most 60 lines.\n" +
        "\n" +
        "Rules:\n" +
        "- Only copy or shorten what the output says. Never add advice, commentary or anything it does not contain.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.ResultOnly;

    /// <summary>Raised for each condensed tool result, with how long it was and how long it became.</summary>
    public event Action<long, long>? Saved;

    /// <summary>Raised when an explore begins, so the chat can list it as a task and stream its steps.</summary>
    public event Action<ExploreRun>? ExploreStarted;

    public async Task<string> ExploreAsync(string question, CancellationToken ct)
    {
        if (!hasApi())
        {
            return Unavailable;
        }

        var run = new ExploreRun(question);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Token);
        ct = linked.Token;
        ExploreStarted?.Invoke(run);
        run.Report("Searching the code...");

        var loop = new SearchAgentLoop(runDirectory, new HelperSearchModel(helper))
        {
            Index = SourceIndex.For(runDirectory),
            Budget = ExploreBudget,
            MaxSteps = ExploreSteps,
            OnStep = step => run.Report(DescribeStep(step)),
        };

        try
        {
            var answer = await loop.RunAsync(question, ct).ConfigureAwait(false);
            run.Report("Writing up the findings...");
            var text = FormatExplore(answer);
            run.Finish(failed: false);
            return text;
        }
        catch (Exception e)
        {
            run.Report(e is OperationCanceledException ? "Cancelled." : $"Failed: {e.Message}");
            run.Finish(failed: e is not OperationCanceledException);
            throw;
        }
    }

    private static string DescribeStep(SearchStep step)
    {
        var text = step.Description.Trim().ReplaceLineEndings(" ");
        if (step.Kind == SearchStepKind.Answer)
        {
            return $"Conclusion: {text}";
        }

        return step.ResultCount > 0 ? $"{text} ({step.ResultCount} results)" : text;
    }

    private static string FormatExplore(SearchAnswer answer)
    {
        var text = new StringBuilder();

        // The write-up opens with the direct answer and was given the summary as input,
        // so printing both says the same thing twice; the summary stands in only when
        // there is no write-up.
        if (answer.Explanation.Trim() is { Length: > 0 } explanation)
        {
            text.AppendLine(explanation);
        }
        else if (answer.Summary.Length > 0)
        {
            text.AppendLine(answer.Summary.Trim());
        }

        // Pointers, not content: the caller reads what it needs from the start line.
        // A list, so a person reads it as easily as the agent does: one line per file.
        if (answer.References.Count > 0)
        {
            text.AppendLine().AppendLine("### Files to read (path:lines - what is declared there)");
            foreach (var reference in answer.References)
            {
                var path = reference.RelativePath.Replace('\\', '/');
                var range = reference.StartLine > 0
                    ? reference.EndLine > reference.StartLine ? $":{reference.StartLine}-{reference.EndLine}" : $":{reference.StartLine}"
                    : "";
                text.Append("- ").Append(path).Append(range).Append(" - ")
                    .AppendLine(DescribeReference(reference));
            }
        }

        if (text.Length == 0)
        {
            text.Append("Nothing relevant was found. Try Grep with a specific identifier.");
        }

        if (answer.StoppedEarly)
        {
            text.AppendLine().Append("(The search ran out of time; treat this as a lead and confirm with Grep/Read.)");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// What the caller gets by opening the file: the declarations in the listed lines, the
    /// search's own reason when it says something about the question, and the words that
    /// find the lines - never bookkeeping such as "ranked by the source index" or "3 matches".
    /// </summary>
    private static string DescribeReference(SearchReference reference)
    {
        var reason = reference.Reason.ReplaceLineEndings(" ").Trim();
        var bookkeeping = reason.Length == 0
            || reason is "ranked by the source index" or "read by the model" or "project setup"
            || System.Text.RegularExpressions.Regex.IsMatch(reason, @"^\d+ (matches|match|matching lines)$");

        var parts = new List<string>();
        if (reference.Contains.Count > 0)
        {
            parts.Add($"declares {string.Join("; ", reference.Contains)}");
        }

        if (!bookkeeping)
        {
            parts.Add(reason);
        }

        if (reference.Find.Count > 0)
        {
            parts.Add($"grep {string.Join(", ", reference.Find.Select(k => $"`{k}`"))} to locate the lines");
        }

        return parts.Count > 0 ? string.Join(". ", parts) : "matched the question";
    }

    public async Task<string?> DigestAsync(string command, string output, CancellationToken ct)
    {
        if (!hasApi() || !AppSettings.TokenSaverDigest)
        {
            return null;
        }

        // The end of a log carries the verdict; keep both ends when it is too long to send whole.
        var input = output.Length <= DigestInputChars
            ? output
            : $"{output[..(DigestInputChars / 2)]}\n[... middle of the output not shown ...]\n{output[^(DigestInputChars / 2)..]}";
        var digest = ModelOutput.CleanText(await helper.CompleteAsync(
            DigestPrompt, $"Command: {command}\n\n{PromptRules.Tag("output", input)}", LongReplyTokens, ct).ConfigureAwait(false));
        return digest.Length == 0 ? null : digest;
    }

    public void RecordSaved(long beforeChars, long afterChars)
    {
        if (beforeChars > afterChars)
        {
            Saved?.Invoke(beforeChars, afterChars);
        }
    }
}
