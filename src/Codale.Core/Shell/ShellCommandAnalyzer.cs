using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>How risky a shell command is, lowest first. A model may raise a level, never lower it.</summary>
public enum ShellRisk
{
    Safe,
    Caution,
    Danger,
}

/// <summary>One rule a command matched: what it is, why it matters, and the segment that matched.</summary>
public sealed record ShellFinding(ShellRisk Level, string Rule, string Reason, string Segment);

/// <summary>
/// The deterministic shell risk check: the rules in <c>shell_risk_rules.json</c> (the same file the
/// model's training data was labelled with) matched against the whole command and each segment of it.
/// "Safe" only means no rule matched; it is the authority, and a model's opinion can only raise it.
/// </summary>
public static class ShellCommandAnalyzer
{
    private sealed record Rule(string Id, ShellRisk Level, string Reason, Regex Pattern, bool RequiresOutsideCwd);

    private static readonly Lazy<IReadOnlyList<Rule>> Rules = new(LoadRules);

    private static readonly Regex AbsolutePath = new(
        @"(?:^|\s|[""'])((?:[a-zA-Z]:\\|\\\\|/)[^\s""']*|\.\.[\\/][^\s""']*)",
        RegexOptions.Compiled);

    /// <summary>Every rule the command matches, most severe first.</summary>
    public static IReadOnlyList<ShellFinding> Analyze(string command, string? cwd = null)
    {
        var findings = new List<ShellFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in new[] { command.Trim() }.Concat(SplitSegments(command)))
        {
            foreach (var rule in Rules.Value)
            {
                if (seen.Contains(rule.Id) || !rule.Pattern.IsMatch(segment))
                {
                    continue;
                }

                if (rule.RequiresOutsideCwd && !IsOutside(segment, cwd))
                {
                    continue;
                }

                seen.Add(rule.Id);
                findings.Add(new ShellFinding(rule.Level, rule.Id, rule.Reason, segment));
            }
        }

        return [.. findings.OrderByDescending(f => f.Level)];
    }

    /// <summary>The highest level any finding reaches; <see cref="ShellRisk.Safe"/> for none.</summary>
    public static ShellRisk Verdict(IReadOnlyList<ShellFinding> findings) =>
        findings.Count == 0 ? ShellRisk.Safe : findings.Max(f => f.Level);

    public static ShellRisk Verdict(string command, string? cwd = null) => Verdict(Analyze(command, cwd));

    /// <summary>
    /// The command's segments, split on <c>| ; &amp;&amp; ||</c> and line breaks outside single and double quotes.
    /// </summary>
    public static IReadOnlyList<string> SplitSegments(string command)
    {
        var segments = new List<string>();
        var buffer = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote is not null)
            {
                buffer.Append(c);
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                buffer.Append(c);
            }
            else if (command.AsSpan(i).StartsWith("&&") || command.AsSpan(i).StartsWith("||"))
            {
                segments.Add(buffer.ToString());
                buffer.Clear();
                i++;
            }
            else if (c is ';' or '|' or '\n')
            {
                segments.Add(buffer.ToString());
                buffer.Clear();
            }
            else
            {
                buffer.Append(c);
            }
        }

        segments.Add(buffer.ToString());
        return [.. segments.Select(s => s.Trim()).Where(s => s.Length > 0)];
    }

    /// <summary>True when the segment names a path that is not inside <paramref name="cwd"/> (or any absolute path when there is none).</summary>
    private static bool IsOutside(string segment, string? cwd)
    {
        foreach (Match match in AbsolutePath.Matches(segment))
        {
            var path = match.Groups[1].Value;
            if (path.StartsWith("..", StringComparison.Ordinal) || string.IsNullOrEmpty(cwd))
            {
                return true;
            }

            if (!IsInside(Normalize(path), Normalize(cwd)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Backslashes, no trailing slash, and Git Bash's <c>/x/dir</c> spelled as <c>x:\dir</c>.</summary>
    private static string Normalize(string path)
    {
        var text = path.Replace('/', '\\').TrimEnd('\\');
        if (text.Length >= 2 && text[0] == '\\' && char.IsLetter(text[1]) && (text.Length == 2 || text[2] == '\\'))
        {
            text = $"{text[1]}:{text[2..]}";
        }

        return text;
    }

    private static bool IsInside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Rule> LoadRules()
    {
        using var stream = typeof(ShellCommandAnalyzer).Assembly.GetManifestResourceStream("shell_risk_rules.json")
            ?? throw new InvalidOperationException("The shell risk rules are not embedded.");
        using var document = JsonDocument.Parse(stream);
        var rules = new List<Rule>();
        foreach (var rule in document.RootElement.GetProperty("rules").EnumerateArray())
        {
            rules.Add(new Rule(
                rule.GetProperty("id").GetString()!,
                Enum.Parse<ShellRisk>(rule.GetProperty("level").GetString()!, ignoreCase: true),
                rule.GetProperty("reason").GetString()!,
                new Regex(rule.GetProperty("pattern").GetString()!, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                rule.TryGetProperty("requires_outside_cwd", out var outside) && outside.GetBoolean()));
        }

        return rules;
    }
}

/// <summary>A command reduced to the programs it runs, for a chat row that should not print a whole script.</summary>
public static class ShellCommandSummary
{
    private const int MaxPrograms = 5;

    private static readonly Regex Assignment = new(@"^(\$env:)?[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    /// <summary>
    /// Everyday look-around and file tools: their arguments are paths and flags, so the program
    /// name alone says what the row did, even when the command is not a chain.
    /// </summary>
    private static readonly HashSet<string> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reading
        "cat", "head", "tail", "less", "more", "bat", "type", "nl", "od", "xxd", "hexdump", "strings",
        // Searching
        "grep", "egrep", "fgrep", "rg", "ag", "ack", "find", "fd", "locate", "which", "where", "whereis",
        // Listing and inspecting
        "ls", "dir", "tree", "pwd", "stat", "file", "du", "df", "wc", "realpath", "dirname", "basename", "readlink",
        // Text processing
        "sed", "awk", "gawk", "cut", "sort", "uniq", "tr", "paste", "join", "column", "fold", "rev", "tac",
        "diff", "cmp", "comm", "jq", "yq", "xargs", "tee", "echo", "printf", "true", "false", "sleep",
        // Files and folders
        "cd", "pushd", "popd", "mkdir", "rmdir", "touch", "cp", "mv", "ln", "chmod", "chown",
        "md5sum", "sha1sum", "sha256sum", "certutil",
        // PowerShell
        "Get-Content", "gc", "Select-String", "sls", "Get-ChildItem", "gci", "Get-Item", "gi", "Get-Location",
        "Set-Location", "sl", "Test-Path", "Resolve-Path", "Split-Path", "Join-Path", "Get-Command", "gcm",
        "Select-Object", "select", "Where-Object", "ForEach-Object", "foreach", "Measure-Object", "measure",
        "Sort-Object", "Format-Table", "ft", "Format-List", "fl", "Out-String", "Write-Output", "Write-Host",
        "Get-FileHash", "Get-Process", "gps", "New-Item", "ni", "Copy-Item", "cpi", "Move-Item", "mi",
        "ConvertFrom-Json", "ConvertTo-Json",
    };

    /// <summary>
    /// The short form a row shows: a chain, a script or an everyday tool reads as the programs it
    /// runs (<see cref="Summarize"/>); anything else keeps its first line, since its arguments matter.
    /// </summary>
    public static string Shorten(string command)
    {
        if (ShellCommandAnalyzer.SplitSegments(command).Count > 1 || IsEveryday(command))
        {
            return Summarize(command);
        }

        return command.Trim().ReplaceLineEndings("\n").Split('\n')[0];
    }

    /// <summary>True when every program the command runs is in the everyday <see cref="Vocabulary"/>.</summary>
    public static bool IsEveryday(string command)
    {
        var any = false;
        foreach (var segment in ShellCommandAnalyzer.SplitSegments(command))
        {
            if (ProgramOf(segment) is not { Length: > 0 } program || !Vocabulary.Contains(program))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>"cd x &amp;&amp; grep -n a b | head; dotnet build" reads "cd &amp;&amp; grep &amp;&amp; head &amp;&amp; dotnet".</summary>
    public static string Summarize(string command)
    {
        var programs = new List<string>();
        foreach (var segment in ShellCommandAnalyzer.SplitSegments(command))
        {
            if (ProgramOf(segment) is { Length: > 0 } program)
            {
                programs.Add(program);
            }
        }

        if (programs.Count == 0)
        {
            return command.Trim();
        }

        var shown = programs.Take(MaxPrograms);
        return string.Join(" && ", shown) + (programs.Count > MaxPrograms ? " …" : "");
    }

    private static string ProgramOf(string segment)
    {
        foreach (var word in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Assignment.IsMatch(word))
            {
                continue;
            }

            var name = word.Trim('"', '\'', '&', '(');
            name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        return "";
    }
}
