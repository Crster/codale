using System.Text;

using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Tasks;
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
    private const int ExploreSteps = 30;

    private const int ExploreChars = 6_000;
    private const int SectionLines = 40;
    private const int DigestInputChars = 150_000;
    private const int LongReplyTokens = 2048;

    private const string Unavailable =
        "Codale's background-task model is not a BYOK provider, so this tool is off. Use Grep/Glob/Read instead.";

    private const string DigestPrompt =
        "You condense a shell command's output for a coding agent that must act on it. " +
        "Keep every error, warning, failed test, exception and stack frame that names project code verbatim, " +
        "with its file path and line number. Keep counts, totals, exit status and the final result line. " +
        "Drop progress, download, restore and passing-test noise. Reply with plain text, at most 60 lines, no preamble.";

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
        if (answer.Summary.Length > 0)
        {
            text.AppendLine(answer.Summary.Trim());
        }

        if (answer.Explanation.Length > 0 && answer.Explanation.Trim() != answer.Summary.Trim())
        {
            text.AppendLine().AppendLine(answer.Explanation.Trim());
        }

        foreach (var section in answer.Sections)
        {
            if (text.Length > ExploreChars)
            {
                text.AppendLine().Append("(more sections omitted)");
                break;
            }

            var path = section.RelativePath.Length > 0 ? section.RelativePath : section.FilePath;
            text.AppendLine().Append(path).Append(':').Append(section.StartLine).Append('-').Append(section.EndLine);
            if (section.Reason.Length > 0)
            {
                text.Append("  (").Append(section.Reason).Append(')');
            }

            text.AppendLine();
            var code = section.Code.Replace("\r\n", "\n").Split('\n');
            text.AppendLine(string.Join('\n', code.Take(SectionLines)));
            if (code.Length > SectionLines)
            {
                text.AppendLine($"... ({code.Length - SectionLines} more lines in this range)");
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
        var digest = await helper.CompleteAsync(DigestPrompt, $"Command: {command}\n\nOutput:\n{input}", LongReplyTokens, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(digest) ? null : digest.Trim();
    }

    public void RecordSaved(long beforeChars, long afterChars)
    {
        if (beforeChars > afterChars)
        {
            Saved?.Invoke(beforeChars, afterChars);
        }
    }
}
