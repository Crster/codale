using System.Text.RegularExpressions;

namespace Codale.Core.Agents;

/// <summary>
/// Shrinks a shell command's output before the agent reads it - build logs, test runs,
/// package installs - so it costs a fraction of the tokens on this turn and on every turn
/// after, while it sits in the history.
/// </summary>
/// <remarks>
/// Deterministic and lossy only where the loss is noise: progress redraws, ANSI colour,
/// repeated lines, passing tests and restore chatter. A line that reads like a problem
/// (error, failure, exception, warning) is never dropped, only de-duplicated, because
/// that is the line the agent ran the command to see. Small output is left alone.
/// </remarks>
public static partial class ShellOutputCompressor
{
    /// <summary>Output shorter than this is returned untouched: there is nothing worth saving.</summary>
    public const int MinChars = 2000;

    /// <summary>Repeats of one warning code kept before the rest become a count.</summary>
    private const int WarningsPerCode = 5;

    /// <summary>A run of look-alike lines at least this long is shortened to its ends and a count.</summary>
    private const int TemplateRun = 4;

    public static ShellCompression Compress(string command, string stdout, string stderr)
    {
        var kind = Classify(command);
        var newOut = CompressStream(kind, stdout);
        var newErr = CompressStream(kind, stderr);
        return new ShellCompression(newOut, newErr, !ReferenceEquals(newOut, stdout) || !ReferenceEquals(newErr, stderr));
    }

    /// <summary>One stream's output, compressed; the same instance when there was nothing to do.</summary>
    public static string CompressStream(CommandKind kind, string text)
    {
        if (text.Length < MinChars)
        {
            return text;
        }

        var lines = SplitLines(AnsiPattern().Replace(text, ""));

        lines = kind switch
        {
            CommandKind.DotnetBuild => BuildLog(lines),
            CommandKind.Test => TestRun(DedupeDiagnostics(lines)),
            CommandKind.PackageInstall => PackageInstall(lines),
            CommandKind.GitStatus => [.. lines.Where(l => !GitHintPattern().IsMatch(l))],
            _ => lines,
        };

        lines = CollapseNoise(lines);
        lines = CollapseTemplates(lines);
        lines = CollapseRepeats(lines);
        lines = CollapseBlanks(lines);

        var result = string.Join('\n', lines).TrimEnd();
        return result.Length >= text.TrimEnd().Length ? text : result;
    }

    /// <summary>
    /// The first and last lines of a long output with the problem lines from between them,
    /// for output that is still too long once compressed and no model can digest it.
    /// </summary>
    public static string HeadTail(string text, int head = 80, int tail = 80, int maxSignals = 60)
    {
        var lines = SplitLines(text);
        if (lines.Count <= head + tail)
        {
            return text;
        }

        var middle = lines.Skip(head).Take(lines.Count - head - tail).ToList();
        var signals = middle.Where(IsSignal).Take(maxSignals).ToList();
        var omitted = middle.Count - signals.Count;

        var kept = new List<string>(head + tail + signals.Count + 2);
        kept.AddRange(lines.Take(head));
        kept.Add($"[... {omitted} lines omitted{(signals.Count > 0 ? $"; the {signals.Count} error/warning lines among them follow" : "")} ...]");
        kept.AddRange(signals);
        if (signals.Count > 0)
        {
            kept.Add("[...]");
        }

        kept.AddRange(lines.Skip(lines.Count - tail));
        return string.Join('\n', kept);
    }

    public static CommandKind Classify(string command)
    {
        var c = command.ToLowerInvariant();
        if (TestCommandPattern().IsMatch(c))
        {
            return CommandKind.Test;
        }

        if (BuildCommandPattern().IsMatch(c))
        {
            return CommandKind.DotnetBuild;
        }

        if (InstallCommandPattern().IsMatch(c))
        {
            return CommandKind.PackageInstall;
        }

        return GitStatusPattern().IsMatch(c) ? CommandKind.GitStatus : CommandKind.Other;
    }

    /// <summary>A line the agent ran the command to see; never dropped.</summary>
    public static bool IsSignal(string line) => SignalPattern().IsMatch(line);

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            // A progress bar redraws its line with bare carriage returns: only the last frame counts.
            var cr = raw.LastIndexOf('\r');
            lines.Add(cr >= 0 ? raw[(cr + 1)..] : raw);
        }

        return lines;
    }

    /// <summary>
    /// MSBuild prints every diagnostic twice (as it happens and in the summary) and the
    /// same warning once per target framework: keep each once, cap a noisy code, and drop
    /// the per-project progress between them.
    /// </summary>
    private static List<string> BuildLog(List<string> lines)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var perCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var dropped = 0;

        foreach (var line in lines)
        {
            if (DiagnosticPattern().Match(line) is { Success: true } d)
            {
                // The trailing [project.csproj] differs between the two copies; the diagnostic does not.
                var key = ProjectSuffixPattern().Replace(line.Trim(), "");
                if (!seen.Add(key))
                {
                    continue;
                }

                if (d.Groups["level"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase))
                {
                    var code = d.Groups["code"].Value;
                    var count = perCode[code] = perCode.GetValueOrDefault(code) + 1;
                    if (count > WarningsPerCode)
                    {
                        continue;
                    }
                }

                kept.Add(key);
                continue;
            }

            if (IsSignal(line) || BuildSummaryPattern().IsMatch(line))
            {
                kept.Add(line);
            }
            else if (line.Trim().Length > 0)
            {
                dropped++;
            }
        }

        foreach (var (code, count) in perCode.Where(p => p.Value > WarningsPerCode))
        {
            kept.Add($"(+{count - WarningsPerCode} more {code} warnings)");
        }

        if (dropped > 0)
        {
            kept.Insert(0, $"[{dropped} lines of build progress omitted]");
        }

        return kept;
    }

    /// <summary>A test run builds first: its compiler diagnostics are printed twice there too.</summary>
    private static List<string> DedupeDiagnostics(List<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. lines.Where(line => !DiagnosticPattern().IsMatch(line) || seen.Add(ProjectSuffixPattern().Replace(line.Trim(), ""))),
        ];
    }

    /// <summary>Passing tests become a count; failures, their messages and the totals stay.</summary>
    private static List<string> TestRun(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var passed = 0;

        foreach (var line in lines)
        {
            if (PassedTestPattern().IsMatch(line) && !FailWordPattern().IsMatch(line))
            {
                passed++;
            }
            else
            {
                kept.Add(line);
            }
        }

        if (passed > 0)
        {
            kept.Insert(0, $"[{passed} passing test lines omitted]");
        }

        return kept;
    }

    /// <summary>Errors and the closing summary stay; deprecation notices and progress become counts.</summary>
    private static List<string> PackageInstall(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var deprecated = 0;
        var progress = 0;

        foreach (var line in lines)
        {
            if (DeprecatedPattern().IsMatch(line))
            {
                deprecated++;
            }
            else if (InstallProgressPattern().IsMatch(line))
            {
                progress++;
            }
            else
            {
                kept.Add(line);
            }
        }

        if (progress > 0)
        {
            kept.Insert(0, $"[{progress} progress lines omitted]");
        }

        if (deprecated > 0)
        {
            kept.Add($"({deprecated} deprecation warnings omitted)");
        }

        return kept;
    }

    /// <summary>A run of restore/download/compile chatter keeps its first and last line.</summary>
    private static List<string> CollapseNoise(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var j = i;
            while (j < lines.Count && NoisePattern().IsMatch(lines[j]) && !IsSignal(lines[j]))
            {
                j++;
            }

            var run = j - i;
            if (run >= 3)
            {
                kept.Add(lines[i]);
                kept.Add($"[... {run - 2} similar lines ...]");
                kept.Add(lines[j - 1]);
                i = j;
            }
            else
            {
                kept.Add(lines[i]);
                i++;
            }
        }

        return kept;
    }

    /// <summary>
    /// Lines that differ only in their numbers (timestamps, counters, ids) collapse to the
    /// first two, the last and a count. Problem lines are left to the exact-repeat pass.
    /// </summary>
    private static List<string> CollapseTemplates(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var template = Template(lines[i]);
            var j = i + 1;
            if (template.Length > 0 && !IsSignal(lines[i]))
            {
                while (j < lines.Count && Template(lines[j]) == template)
                {
                    j++;
                }
            }

            var run = j - i;
            if (run >= TemplateRun && lines[i] != lines[j - 1])
            {
                kept.Add(lines[i]);
                kept.Add(lines[i + 1]);
                kept.Add($"[... {run - 3} more like these ...]");
                kept.Add(lines[j - 1]);
            }
            else
            {
                kept.AddRange(lines.Skip(i).Take(run));
            }

            i = j;
        }

        return kept;
    }

    private static string Template(string line) =>
        line.Trim().Length < 8 ? "" : DigitsPattern().Replace(line, "#");

    /// <summary>The same line several times in a row is printed once with a count.</summary>
    private static List<string> CollapseRepeats(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var j = i + 1;
            while (j < lines.Count && lines[j] == lines[i])
            {
                j++;
            }

            kept.Add(j - i > 1 && lines[i].Trim().Length > 0 ? $"{lines[i]} (x{j - i})" : lines[i]);
            i = j;
        }

        return kept;
    }

    private static List<string> CollapseBlanks(List<string> lines)
    {
        var kept = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var blank = line.Trim().Length == 0;
            if (blank && (kept.Count == 0 || kept[^1].Trim().Length == 0))
            {
                continue;
            }

            kept.Add(blank ? "" : line.TrimEnd());
        }

        return kept;
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[=>]")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"error|fail|exception|panic|fatal|warn|traceback|assert|denied|not found|cannot|unable", RegexOptions.IgnoreCase)]
    private static partial Regex SignalPattern();

    [GeneratedRegex(@"fail|error|exception", RegexOptions.IgnoreCase)]
    private static partial Regex FailWordPattern();

    [GeneratedRegex(@"\bdotnet\s+test\b|\b(jest|vitest|pytest|playwright\s+test|rspec|phpunit|mocha)\b|\bcargo\s+(test|nextest)\b|\bgo\s+test\b|\b(npm|pnpm|yarn|bun)\s+(run\s+)?test\b|\bpython\s+-m\s+(pytest|unittest)\b")]
    private static partial Regex TestCommandPattern();

    [GeneratedRegex(@"\bdotnet\s+(build|publish|pack|msbuild|restore)\b|\bmsbuild(\.exe)?\b")]
    private static partial Regex BuildCommandPattern();

    [GeneratedRegex(@"\b(npm|pnpm|yarn|bun)\s+(install|i|ci|add)\b|^\s*(yarn|pnpm)\s*$|\bpip3?\s+install\b")]
    private static partial Regex InstallCommandPattern();

    [GeneratedRegex(@"\bgit\s+status\b")]
    private static partial Regex GitStatusPattern();

    [GeneratedRegex(@"^\s*\(use ""git [^""]+""")]
    private static partial Regex GitHintPattern();

    /// <summary>MSBuild's canonical diagnostic: <c>path(line,col): error CS1234: message [project]</c>.</summary>
    [GeneratedRegex(@":\s*(?<level>error|warning)\s+(?<code>[A-Z]+\d+)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(@"\s*\[[^\[\]]+\.(cs|vb|fs)proj(::[^\]]*)?\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectSuffixPattern();

    [GeneratedRegex(@"Build (succeeded|FAILED)|\d+ (Warning|Error)\(s\)|Time Elapsed|^\s*(Build|Restore) (succeeded|failed|complete)|succeeded with \d+ warning|failed with \d+ error", RegexOptions.IgnoreCase)]
    private static partial Regex BuildSummaryPattern();

    [GeneratedRegex(@"^\s*(✓|✔|√|Passed\s|PASS\s|ok\s+\S|--- PASS|test .+ \.\.\. ok\s*$|.+\sPASSED(\s|$))")]
    private static partial Regex PassedTestPattern();

    [GeneratedRegex(@"^\s*npm (WARN|warn) deprecated|^\s*warning .*deprecated", RegexOptions.IgnoreCase)]
    private static partial Regex DeprecatedPattern();

    [GeneratedRegex(@"^\s*(Progress: resolved|Packages: [+-]|[+-]{8,}\s*$|Downloading\b|Downloaded\b|Already up to date|Resolving:|Fetching\b)")]
    private static partial Regex InstallProgressPattern();

    [GeneratedRegex(@"^\s*(Restor(e|ing|ed)\b|Determining projects|Download(ing|ed)\b|Compiling\b|Checking\b|Installing\b|Resolving\b|Fetching\b|Unpacking\b|Preparing\b|Collecting\b|Requirement already satisfied|Using cached|Building wheel|Progress:|\[\d+/\d+\]|\s*\d+%)", RegexOptions.IgnoreCase)]
    private static partial Regex NoisePattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();
}

public enum CommandKind
{
    Other,
    DotnetBuild,
    Test,
    PackageInstall,
    GitStatus,
}

/// <summary>A command's output after <see cref="ShellOutputCompressor.Compress"/>; <see cref="Changed"/> is false when nothing was worth doing.</summary>
public sealed record ShellCompression(string Stdout, string Stderr, bool Changed);
