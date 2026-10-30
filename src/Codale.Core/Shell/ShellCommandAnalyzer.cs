using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Codale.Core.Helper;

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
/// What a shell command is going to do (<see cref="Description"/>) and its safety rating
/// (<see cref="Risk"/> and <see cref="Reason"/>), combining deterministic risk rules with the helper model.
/// </summary>
public sealed record ShellAssessment(string Description, ShellRisk Risk, string Reason)
{
    /// <summary>"Safe - reads git status only" or "Danger - deletes files outside the working directory".</summary>
    public string FormatAssessment() =>
        string.IsNullOrWhiteSpace(Reason) ? Risk.ToString() : $"{Risk} - {Reason}";

    /// <summary>"Danger: deletes files outside the working directory".</summary>
    public string FormatRiskNote() =>
        string.IsNullOrWhiteSpace(Reason) ? Risk.ToString() : $"{Risk}: {Reason}";
}

/// <summary>
/// The deterministic shell risk check: the rules in <c>shell_risk_rules.json</c> (the same file the
/// model's training data was labelled with) matched against the whole command and each segment of it.
/// "Safe" only means no rule matched; it is the authority, and a model's opinion can only raise it.
/// </summary>
public static class ShellCommandAnalyzer
{
    private const int MaxCommandChars = 2_000;
    private const int MaxDescriptionChars = 140;
    private const int MaxReasonChars = 120;

    public const string AssessSystemPrompt =
        "You describe what a shell command will do and rate its safety for a developer reviewing or inspecting it: " +
        "call assess_command once with description, risk and reason.\n" +
        "\n" +
        "description:\n" +
        "- One brief, accurate sentence (under 90 characters) stating what the command accomplishes " +
        "(e.g. \"Build the App project for x64\", \"Search source files for ViewModel references\", " +
        "\"Delete local bin and obj build folders\", \"Start the local dev server on port 3000\").\n" +
        "- Explain the intent and target of the command in plain English; NEVER just list the program names " +
        "(never write \"Runs cd, grep\" or \"cd && grep\").\n" +
        "\n" +
        "risk:\n" +
        "- safe: read-only inspection, searching, listing, building or running tests inside the workspace with no destructive side effects.\n" +
        "- caution: modifies or deletes workspace files, installs packages, kills processes, rewrites local git history, or redirects output to files.\n" +
        "- danger: modifies or deletes files outside the working directory, force-pushes, runs arbitrary remote scripts, alters system/registry/firewall settings, or risks data loss.\n" +
        "- If <deterministic_findings> is present, risk MUST be at least as high as the highest finding listed.\n" +
        "\n" +
        "reason:\n" +
        "- A short phrase (under 80 characters) explaining why the command has that safety rating " +
        "(e.g. \"reads git status only\", \"deletes build output folders in the workspace\", \"deletes files outside the working directory\").\n" +
        "\n" +
        "Rules:\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.CallOnly;

    public static readonly ToolDefinition AssessTool = new()
    {
        Name = "assess_command",
        Description = "Describe what the shell command will do and rate its safety.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "description",
                Description = "brief, accurate description of what the command does (not just program names)",
                Required = true,
            },
            new ToolParameter
            {
                Name = "risk",
                Description = "safety rating of the command",
                Required = true,
                AllowedValues = ["safe", "caution", "danger"],
            },
            new ToolParameter
            {
                Name = "reason",
                Description = "short phrase explaining the safety rating",
                Required = true,
            },
        ],
    };

    private sealed record Rule(string Id, ShellRisk Level, string Reason, Regex Pattern, bool RequiresOutsideCwd);

    private static readonly Lazy<IReadOnlyList<Rule>> Rules = new(LoadRules);

    private static readonly ConcurrentDictionary<(string Command, string Cwd), ShellAssessment> Cache = new();

    private static readonly Regex AbsolutePath = new(
        @"(?:^|\s|[""'])((?:[a-zA-Z]:\\|\\\\|/)[^\s""']*|\.\.[\\/][^\s""']*)",
        RegexOptions.Compiled);

    /// <summary>Every rule the command matches, most severe first.</summary>
    public static IReadOnlyList<ShellFinding> Analyze(string command, string? cwd = null, IReadOnlyList<string>? extraRoots = null)
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

                if (rule.RequiresOutsideCwd && !IsOutside(segment, cwd, extraRoots))
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
    /// Immediate, deterministic assessment of <paramref name="command"/> before or without the helper model:
    /// uses <paramref name="hint"/> when it is a real description (not a raw command or program list),
    /// otherwise synthesizes a concise human-readable description from the command's segments.
    /// </summary>
    public static ShellAssessment Assess(string command, string? cwd = null, string? hint = null, IReadOnlyList<string>? extraRoots = null)
    {
        var trimmed = command.Trim();
        if (Cache.TryGetValue((trimmed, CacheScope(cwd, extraRoots)), out var cached))
        {
            return !string.IsNullOrWhiteSpace(hint) && IsMeaningfulHint(hint, trimmed) && string.IsNullOrWhiteSpace(cached.Description)
                ? cached with { Description = CleanSentence(hint, MaxDescriptionChars) }
                : cached;
        }

        var findings = Analyze(trimmed, cwd, extraRoots);
        var risk = Verdict(findings);
        var reason = findings.Count > 0
            ? string.Join("; ", findings.Select(f => f.Reason).Distinct())
            : ShellCommandSummary.DescribeSafetyReason(trimmed);
        var description = !string.IsNullOrWhiteSpace(hint) && IsMeaningfulHint(hint, trimmed)
            ? CleanSentence(hint, MaxDescriptionChars)
            : ShellCommandSummary.DescribeIntent(trimmed);

        return new ShellAssessment(description, risk, reason);
    }

    /// <summary>
    /// Uses <paramref name="model"/> to describe what <paramref name="command"/> will do and rate its safety,
    /// anchored by the deterministic rules in <c>shell_risk_rules.json</c> (the model may raise the risk level,
    /// never lower it). Falls back to <see cref="Assess"/> when <paramref name="model"/> is unavailable or fails.
    /// </summary>
    public static async Task<ShellAssessment> AssessAsync(
        IHelperModel? model,
        string command,
        string? cwd = null,
        string? hint = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? extraRoots = null)
    {
        var trimmed = command.Trim();
        var baseline = Assess(trimmed, cwd, hint, extraRoots);
        if (trimmed.Length == 0 || model is not { IsAvailable: true })
        {
            return baseline;
        }

        var key = (trimmed, CacheScope(cwd, extraRoots));
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var findings = Analyze(trimmed, cwd, extraRoots);
            var conversation = BuildConversation(trimmed, cwd, hint, findings);
            var call = await model.CallToolAsync(AssessSystemPrompt, conversation, [AssessTool], ct).ConfigureAwait(false);
            if (call is not { Tool: "assess_command" })
            {
                return baseline;
            }

            var result = Interpret(call, baseline, findings);
            Cache[key] = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return baseline;
        }
    }

    /// <summary>Builds the tagged user prompt for <see cref="AssessTool"/>.</summary>
    public static string BuildConversation(
        string command,
        string? cwd = null,
        string? hint = null,
        IReadOnlyList<ShellFinding>? findings = null)
    {
        var clipped = command.Length > MaxCommandChars ? command[..MaxCommandChars] : command;
        var builder = new StringBuilder();
        builder.Append(PromptRules.Tag("command", clipped));

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            builder.Append('\n').Append(PromptRules.Tag("cwd", cwd.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(hint) && IsMeaningfulHint(hint, command))
        {
            builder.Append('\n').Append(PromptRules.Tag("agent_hint", hint.Trim()));
        }

        if (findings is { Count: > 0 })
        {
            var lines = string.Join("\n", findings.Select(f => $"{f.Level.ToString().ToLowerInvariant()}: {f.Reason} ({f.Rule})"));
            builder.Append('\n').Append(PromptRules.Tag("deterministic_findings", lines));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Combines the model's <see cref="ToolCall"/> with the deterministic <paramref name="baseline"/>:
    /// the risk level is <c>max(baseline.Risk, modelRisk)</c> so a model can raise the safety level
    /// but never lower a rule match.
    /// </summary>
    public static ShellAssessment Interpret(
        ToolCall call,
        ShellAssessment baseline,
        IReadOnlyList<ShellFinding>? findings = null)
    {
        var modelDesc = CleanSentence(call.GetString("description"), MaxDescriptionChars);
        var modelReason = CleanReason(call.GetString("reason"), MaxReasonChars);
        var modelRisk = call.GetString("risk")?.Trim().ToLowerInvariant() switch
        {
            "danger" => ShellRisk.Danger,
            "caution" => ShellRisk.Caution,
            _ => ShellRisk.Safe,
        };

        var finalRisk = (ShellRisk)Math.Max((int)baseline.Risk, (int)modelRisk);
        var description = modelDesc.Length > 0 ? modelDesc : baseline.Description;

        string reason;
        if (findings is { Count: > 0 })
        {
            var ruleReasons = string.Join("; ", findings.Select(f => f.Reason).Distinct());
            reason = modelRisk > baseline.Risk && modelReason.Length > 0 && !ruleReasons.Contains(modelReason, StringComparison.OrdinalIgnoreCase)
                ? $"{ruleReasons}; {modelReason}"
                : ruleReasons;
        }
        else
        {
            reason = modelReason.Length > 0 ? modelReason : baseline.Reason;
        }

        return new ShellAssessment(description, finalRisk, reason);
    }

    private static bool IsMeaningfulHint(string hint, string command)
    {
        var clean = hint.Trim();
        if (clean.Length == 0 ||
            string.Equals(clean, command, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, ShellCommandSummary.Summarize(command), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, ShellCommandSummary.Shorten(command), StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Runs ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string CleanSentence(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var clean = text.ReplaceLineEndings(" ").Trim().Trim('"', '\'');
        if (clean.Length == 0)
        {
            return "";
        }

        clean = char.ToUpperInvariant(clean[0]) + clean[1..];
        return clean.Length > maxChars ? clean[..maxChars].TrimEnd() + "…" : clean;
    }

    private static string CleanReason(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var clean = text.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '.');
        foreach (var prefix in new[] { "safe:", "caution:", "danger:", "safe -", "caution -", "danger -" })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[prefix.Length..].Trim();
                break;
            }
        }

        return clean.Length > maxChars ? clean[..maxChars].TrimEnd() + "…" : clean;
    }

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

    /// <summary>True when the segment names a path that is not inside <paramref name="cwd"/> or an extra root (or any absolute path when there is no cwd).</summary>
    private static bool IsOutside(string segment, string? cwd, IReadOnlyList<string>? extraRoots = null)
    {
        foreach (Match match in AbsolutePath.Matches(segment))
        {
            var path = match.Groups[1].Value;
            if (path.StartsWith("..", StringComparison.Ordinal) || string.IsNullOrEmpty(cwd))
            {
                return true;
            }

            var normalized = Normalize(path);
            if (!IsInside(normalized, Normalize(cwd)) && !(extraRoots?.Any(root => IsInside(normalized, Normalize(root))) ?? false))
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

    /// <summary>Verdicts depend on the folders the agent may work in, so they are cached per folder set.</summary>
    private static string CacheScope(string? cwd, IReadOnlyList<string>? extraRoots) =>
        extraRoots is { Count: > 0 } ? $"{cwd}|{string.Join('|', extraRoots)}" : cwd ?? "";

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
        command = UnwrapShellCommand(command);
        if (ShellCommandAnalyzer.SplitSegments(command).Count > 1 || IsEveryday(command))
        {
            return Summarize(command);
        }

        return command.Trim().ReplaceLineEndings("\n").Split('\n')[0];
    }

    // Codex reports the shell executable and its -Command argument as the command.
    // Remove that transport wrapper only for the compact row; the detail retains the original.
    private static string UnwrapShellCommand(string command)
    {
        var match = Regex.Match(command.Trim(),
            "^(?:\"(?<exe>[^\"]+)\"|(?<exe>\\S+))\\s+-(?:command|c)\\s+(?<body>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return command;
        var executable = match.Groups["exe"].Value.Replace('\\', '/').Split('/').Last();
        if (!executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) &&
            !executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) &&
            !executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
            !executable.Equals("powershell", StringComparison.OrdinalIgnoreCase)) return command;
        var body = match.Groups["body"].Value.Trim();
        if (body.Length >= 2 && (body[0] == '\'' && body[^1] == '\'' || body[0] == '\"' && body[^1] == '\"'))
            body = body[1..^1];
        return body;
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

    /// <summary>
    /// A concise plain-English description of what <paramref name="command"/> does, used before or
    /// when the helper model has not answered yet, so the UI never merely lists program names.
    /// </summary>
    public static string DescribeIntent(string command)
    {
        var actions = new List<string>();
        foreach (var segment in ShellCommandAnalyzer.SplitSegments(command))
        {
            var phrase = DescribeSegment(segment);
            if (phrase.Length > 0 && !actions.Contains(phrase, StringComparer.OrdinalIgnoreCase))
            {
                actions.Add(phrase);
            }
        }

        if (actions.Count == 0)
        {
            return "Run shell command";
        }

        // Skip a leading "change directory" when followed by the real work.
        if (actions.Count > 1 && actions[0].StartsWith("Change directory", StringComparison.OrdinalIgnoreCase))
        {
            actions.RemoveAt(0);
        }

        var combined = actions.Count == 1
            ? actions[0]
            : actions[0] + ", then " + string.Join(", then ", actions.Skip(1).Take(2).Select(LowerFirst));
        return combined.Length > 140 ? combined[..140].TrimEnd() + "…" : combined;
    }

    /// <summary>A brief reason for a <see cref="ShellRisk.Safe"/> command before the helper model replies.</summary>
    public static string DescribeSafetyReason(string command)
    {
        if (IsEveryday(command))
        {
            return "reads or inspects workspace files only";
        }

        var first = ShellCommandAnalyzer.SplitSegments(command)
            .Select(ProgramOf)
            .FirstOrDefault(p => p.Length > 0 && !p.Equals("cd", StringComparison.OrdinalIgnoreCase) && !p.Equals("Set-Location", StringComparison.OrdinalIgnoreCase))
            ?? "";

        return first.ToLowerInvariant() switch
        {
            "git" => "runs git in the workspace without destructive flags",
            "dotnet" or "msbuild" or "cargo" or "go" or " tsc" => "builds or tests the project locally",
            "npm" or "pnpm" or "yarn" or "bun" => "runs a project script or test locally",
            "pytest" or "jest" or "vitest" or "xunit" => "runs tests locally",
            _ => "runs within the workspace with no risky flags matched",
        };
    }

    private static string DescribeSegment(string segment)
    {
        var words = Tokenize(segment);
        if (words.Count == 0)
        {
            return "";
        }

        var prog = ProgramOf(segment);
        if (prog.Length == 0)
        {
            return "";
        }

        var args = words.SkipWhile(w => Assignment.IsMatch(w)).Skip(1).ToList();
        var nonFlags = args.Where(a => !a.StartsWith('-')).ToList();
        var firstArg = nonFlags.FirstOrDefault() ?? "";
        var lastArg = nonFlags.LastOrDefault() ?? "";

        switch (prog.ToLowerInvariant())
        {
            case "cd" or "pushd" or "set-location" or "sl":
                return firstArg.Length > 0 ? $"Change directory to {ShortTarget(firstArg)}" : "Change working directory";
            case "grep" or "egrep" or "fgrep" or "rg" or "ag" or "ack" or "select-string" or "sls":
                return nonFlags.Count switch
                {
                    >= 2 => $"Search {ShortTarget(lastArg)} for \"{ShortTarget(nonFlags[0])}\"",
                    1 => $"Search for \"{ShortTarget(nonFlags[0])}\"",
                    _ => "Search text in files",
                };
            case "find" or "fd" or "get-childitem" or "gci":
                return firstArg.Length > 0 ? $"Find files in {ShortTarget(firstArg)}" : "List and find files";
            case "ls" or "dir" or "tree":
                return firstArg.Length > 0 ? $"List contents of {ShortTarget(firstArg)}" : "List directory contents";
            case "cat" or "type" or "bat" or "get-content" or "gc":
                return firstArg.Length > 0 ? $"Read {ShortTarget(firstArg)}" : "Read file contents";
            case "head":
                return lastArg.Length > 0 ? $"Read first lines of {ShortTarget(lastArg)}" : "Read first lines of output";
            case "tail":
                return lastArg.Length > 0 ? $"Read last lines of {ShortTarget(lastArg)}" : "Read last lines of output";
            case "sed" or "awk" or "gawk":
                return nonFlags.Count >= 2 ? $"Extract or transform text in {ShortTarget(lastArg)}" : "Transform text stream";
            case "wc" or "measure-object" or "measure":
                return firstArg.Length > 0 ? $"Count lines or items in {ShortTarget(firstArg)}" : "Count lines or items";
            case "sort" or "sort-object":
                return "Sort output lines";
            case "uniq":
                return "Filter duplicate lines";
            case "diff" or "cmp":
                return nonFlags.Count >= 2 ? $"Compare {ShortTarget(nonFlags[0])} and {ShortTarget(nonFlags[1])}" : "Compare files";
            case "rm" or "del" or "erase" or "rmdir" or "rd" or "remove-item" or "ri":
                return nonFlags.Count > 0
                    ? $"Delete {string.Join(", ", nonFlags.Take(3).Select(ShortTarget))}"
                    : "Delete files or directories";
            case "mkdir" or "md" or "new-item" or "ni":
                return firstArg.Length > 0 ? $"Create {ShortTarget(firstArg)}" : "Create directory or file";
            case "cp" or "copy" or "copy-item" or "cpi":
                return nonFlags.Count >= 2 ? $"Copy {ShortTarget(nonFlags[0])} to {ShortTarget(lastArg)}" : "Copy files";
            case "mv" or "move" or "ren" or "move-item" or "mi":
                return nonFlags.Count >= 2 ? $"Move {ShortTarget(nonFlags[0])} to {ShortTarget(lastArg)}" : "Move or rename files";
            case "dotnet":
                return DescribeDotnet(args, nonFlags);
            case "git":
                return DescribeGit(args, nonFlags);
            case "npm" or "pnpm" or "yarn" or "bun":
                return nonFlags.Count switch
                {
                    >= 2 when nonFlags[0].Equals("run", StringComparison.OrdinalIgnoreCase) => $"Run {prog} script '{nonFlags[1]}'",
                    >= 1 => $"Run {prog} {string.Join(" ", nonFlags.Take(2))}",
                    _ => $"Run {prog}",
                };
            case "python" or "python3" or "py" or "node" or "pwsh" or "powershell" or "bash" or "sh":
                return firstArg.Length > 0 ? $"Run {prog} on {ShortTarget(firstArg)}" : $"Run {prog} command";
            case "curl" or "wget" or "invoke-webrequest" or "iwr" or "invoke-restmethod" or "irm":
                return firstArg.Length > 0 ? $"Fetch {ShortTarget(firstArg)}" : "Make HTTP request";
            case "taskkill" or "stop-process" or "kill":
                return "Terminate running process";
            default:
                return nonFlags.Count > 0
                    ? $"Run {prog} on {string.Join(" ", nonFlags.Take(2).Select(ShortTarget))}"
                    : $"Run {prog}";
        }
    }

    private static string DescribeDotnet(List<string> args, List<string> nonFlags)
    {
        if (nonFlags.Count == 0)
        {
            return "Run .NET CLI";
        }

        var verb = nonFlags[0].ToLowerInvariant();
        var target = nonFlags.Skip(1).FirstOrDefault();
        return verb switch
        {
            "build" => target is not null ? $"Build {ShortTarget(target)}" : "Build the .NET solution",
            "test" => target is not null ? $"Run tests in {ShortTarget(target)}" : "Run .NET unit tests",
            "run" => target is not null ? $"Run {ShortTarget(target)}" : "Run the .NET application",
            "restore" => "Restore NuGet packages",
            "clean" => "Clean build outputs",
            "publish" => "Publish the .NET application",
            _ => $"Run dotnet { string.Join(" ", args.Take(3)) }",
        };
    }

    private static string DescribeGit(List<string> args, List<string> nonFlags)
    {
        if (nonFlags.Count == 0)
        {
            return "Run Git command";
        }

        var sub = nonFlags[0].ToLowerInvariant();
        return sub switch
        {
            "status" => "Check Git working tree status",
            "diff" => "Inspect uncommitted Git changes",
            "log" => "View recent Git commit history",
            "show" => "Show Git commit details",
            "branch" => "List or manage Git branches",
            "checkout" or "switch" => nonFlags.Count > 1 ? $"Switch Git branch to {ShortTarget(nonFlags[1])}" : "Switch Git branch",
            "add" => "Stage files for Git commit",
            "commit" => "Create a Git commit",
            "push" => args.Any(a => a is "-f" or "--force" or "--force-with-lease") ? "Force-push commits to remote" : "Push commits to remote",
            "pull" or "fetch" => $"Run git {sub} from remote",
            "reset" => args.Contains("--hard") ? "Reset working tree and discard local changes" : "Reset Git staging state",
            "clean" => "Remove untracked files from working tree",
            "stash" => "Stash local Git changes",
            _ => $"Run git {string.Join(" ", nonFlags.Take(2))}",
        };
    }

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in segment)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string ShortTarget(string token)
    {
        var clean = token.Trim('"', '\'', ';');
        if (clean.Length > 40 && clean.IndexOfAny(['/', '\\']) >= 0)
        {
            var name = clean[(clean.LastIndexOfAny(['/', '\\']) + 1)..];
            if (name.Length > 0)
            {
                return name;
            }
        }

        return clean.Length > 40 ? clean[..40] + "…" : clean;
    }

    private static string LowerFirst(string phrase) =>
        phrase.Length > 1 && !char.IsUpper(phrase[1])
            ? char.ToLowerInvariant(phrase[0]) + phrase[1..]
            : phrase;

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
