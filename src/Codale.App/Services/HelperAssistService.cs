using System.Text;

using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Tasks;
using Codale.Search;

namespace Codale.App.Services;

/// <summary>
/// The app's end of <see cref="ITaskAssist"/>: explore, ask_files and output digests for a
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
    private const int AskFilesChars = 200_000;
    private const int DigestInputChars = 150_000;
    private const int LongReplyTokens = 2048;

    private const string Unavailable =
        "Codale's background-task model is not a BYOK provider, so this tool is off. Use Grep/Glob/Read instead.";

    private const string AskFilesPrompt =
        "You answer a coding agent's question about the files below, so it does not have to read them itself. " +
        "Answer precisely and briefly. Cite locations as path:line (lines are numbered in the input). " +
        "Quote identifiers, signatures and values exactly as written. If the answer is not in these files, say so plainly; never guess.";

    private const string DigestPrompt =
        "You condense a shell command's output for a coding agent that must act on it. " +
        "Keep every error, warning, failed test, exception and stack frame that names project code verbatim, " +
        "with its file path and line number. Keep counts, totals, exit status and the final result line. " +
        "Drop progress, download, restore and passing-test noise. Reply with plain text, at most 60 lines, no preamble.";

    /// <summary>Raised for each condensed tool result, with how long it was and how long it became.</summary>
    public event Action<long, long>? Saved;

    public async Task<string> ExploreAsync(string question, CancellationToken ct)
    {
        if (!hasApi())
        {
            return Unavailable;
        }

        var loop = new SearchAgentLoop(runDirectory, new HelperSearchModel(helper)) { Budget = ExploreBudget, MaxSteps = ExploreSteps };
        var answer = await loop.RunAsync(question, ct).ConfigureAwait(false);
        return FormatExplore(answer);
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

    public async Task<string> AskFilesAsync(string question, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (!hasApi())
        {
            return Unavailable;
        }

        var root = Path.GetFullPath(runDirectory);
        var realRoot = ResolveLinks(root);
        var input = new StringBuilder();
        var skipped = new List<string>();

        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(root, raw));

            // The model only reads inside the session's own folder - by real path, so a
            // junction or symlink inside the project cannot lead out of it.
            if (!ProjectPaths.IsInside(root, full) || !File.Exists(full) || !ProjectPaths.IsInside(realRoot, ResolveLinks(full)))
            {
                skipped.Add(raw);
                continue;
            }

            if (input.Length >= AskFilesChars)
            {
                skipped.Add(raw);
                continue;
            }

            input.Append("=== ").Append(Path.GetRelativePath(root, full)).AppendLine(" ===");
            var number = 0;
            foreach (var line in File.ReadLines(full))
            {
                input.Append(++number).Append(": ").AppendLine(line);
                if (input.Length >= AskFilesChars)
                {
                    input.AppendLine("(file cut short here)");
                    break;
                }
            }
        }

        if (input.Length == 0)
        {
            return $"None of these files could be read inside the project: {string.Join(", ", skipped)}.";
        }

        var answer = await helper.CompleteAsync(AskFilesPrompt, $"Question: {question}\n\n{input}", LongReplyTokens, ct).ConfigureAwait(false);
        return skipped.Count == 0 ? answer : $"{answer}\n\n(Not read - outside the project, missing or over the size limit: {string.Join(", ", skipped)})";
    }

    /// <summary>The path with every symlink and junction along it replaced by its target (unresolvable links are left as written).</summary>
    private static string ResolveLinks(string path)
    {
        var current = Path.GetPathRoot(path) ?? "";
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var part in path[current.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (IOException)
            {
                // Not resolvable: judge it by the literal path.
            }
        }

        return current;
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
