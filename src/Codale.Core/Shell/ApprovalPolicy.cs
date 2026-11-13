using System.Text.Json;

namespace Codale.Core.Shell;

/// <summary>What Codale does with an agent's request to run a tool.</summary>
public enum ApprovalVerdict
{
    /// <summary>Answer yes without asking the reader.</summary>
    Allow,

    /// <summary>Show the reader a confirmation card.</summary>
    Ask,

    /// <summary>Answer no, with a reason the agent can act on.</summary>
    Deny,
}

public sealed record ApprovalPlan(ApprovalVerdict Verdict, string Reason = "")
{
    public static readonly ApprovalPlan Allow = new(ApprovalVerdict.Allow);
    public static readonly ApprovalPlan Ask = new(ApprovalVerdict.Ask);
    public static ApprovalPlan Deny(string reason) => new(ApprovalVerdict.Deny, reason);
}

/// <summary>
/// Codale is the only permission gate: the CLIs run with no sandbox of their own and hand every
/// approval to the host, which answers by the chosen mode.
/// <list type="bullet">
/// <item><b>Ask, Plan</b> - read-only: reads, web lookups and read-only shell run; no writes, no
/// unknown shell (denied).</item>
/// <item><b>Interactive</b> - reads and read-only shell run; every write asks.</item>
/// <item><b>Auto</b> - everything runs except dangerous shell (which reaches outside the project or
/// can lose data), edits outside the project, and high-risk MCP tools, which ask.</item>
/// <item><b>Full</b> - not governed here: everything is approved.</item>
/// </list>
/// </summary>
public static class ApprovalPolicy
{
    public const string AskMode = "ask";
    public const string AutomaticMode = "automatic";

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "Read", "Glob", "Grep", "LS", "WebSearch", "WebFetch", "NotebookRead", "TodoWrite", "TaskOutput", "BashOutput",
        "ListMcpResources", "ReadMcpResource", "ToolSearch", "Skill", "Task", "Agent", "EnterPlanMode", "TaskCreate",
        "TaskUpdate", "TaskList", "TaskGet", "SlashCommand",
    };

    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    private static readonly string[] ProtectedFolders = [".git", ".claude", ".codex", ".ssh", ".gnupg", ".aws", ".azure"];

    /// <summary>The modes this policy decides; others (Full, accept-edits, unset) keep their own handling.</summary>
    public static bool Governs(string mode) => mode is AskMode or "plan" or "manual" or "default" or AutomaticMode;

    /// <summary>
    /// The verdict for one tool request. <paramref name="input"/> is the tool's JSON input,
    /// <paramref name="highRiskMcp"/> marks an MCP tool Codale knows can reach beyond the project.
    /// </summary>
    public static ApprovalPlan Decide(
        string mode,
        string toolName,
        string? shellCommand,
        JsonElement input,
        string? cwd,
        IReadOnlyList<string>? extraRoots,
        bool highRiskMcp = false)
    {
        var readOnlyMode = mode is AskMode or "plan";
        var automatic = mode == AutomaticMode;

        // Questions and plans need the reader's answer in every mode.
        if (toolName is "AskUserQuestion" or "ExitPlanMode")
        {
            return ApprovalPlan.Ask;
        }

        if (ReadTools.Contains(toolName))
        {
            return ApprovalPlan.Allow;
        }

        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            if (highRiskMcp)
            {
                return readOnlyMode ? ApprovalPlan.Deny(ReadOnlyReason(mode, "tool")) : ApprovalPlan.Ask;
            }

            return readOnlyMode ? ApprovalPlan.Ask : ApprovalPlan.Allow;
        }

        if (toolName is "Bash" or "PowerShell")
        {
            return DecideShell(mode, shellCommand, cwd, extraRoots);
        }

        if (EditTools.Contains(toolName) || toolName == "Edit")
        {
            var paths = FilePaths(input);
            if (mode == "plan" && paths.Count > 0 && paths.All(IsPlanFile))
            {
                return ApprovalPlan.Allow;
            }

            if (readOnlyMode)
            {
                return ApprovalPlan.Deny(ReadOnlyReason(mode, "edit"));
            }

            return automatic && paths.Count > 0 && paths.All(path => IsSafeToEdit(path, cwd, extraRoots))
                ? ApprovalPlan.Allow
                : ApprovalPlan.Ask;
        }

        return readOnlyMode ? ApprovalPlan.Deny(ReadOnlyReason(mode, "action")) : ApprovalPlan.Ask;
    }

    private static ApprovalPlan DecideShell(string mode, string? command, string? cwd, IReadOnlyList<string>? extraRoots)
    {
        var classification = ShellCommandClassifier.Classify(command, cwd, extraRoots);
        if (classification.Class == ShellClass.ReadOnly)
        {
            return ApprovalPlan.Allow;
        }

        if (mode is AskMode or "plan")
        {
            return ApprovalPlan.Deny(ReadOnlyReason(mode, "shell command", classification));
        }

        // Auto mode only stops for Danger: Safe and Caution commands (builds, project writes and deletes,
        // programs the database does not know) run without a card.
        return mode == AutomaticMode && classification.Class != ShellClass.Danger ? ApprovalPlan.Allow : ApprovalPlan.Ask;
    }

    private static string ReadOnlyReason(string mode, string what, ShellClassification? why = null)
    {
        var name = mode == AskMode ? "Ask" : "Plan";
        var detail = why is null ? "" : $" ({why.Reason})";
        var next = mode == AskMode
            ? "Reading files, searching the project or the web, and read-only commands are fine. Answer in chat instead - describe the change and show the code."
            : "Reading files, searching, and read-only commands are fine. Put the change in the plan instead of making it.";
        return $"{name} mode is read-only: this {what} was not run{detail}. {next}";
    }

    /// <summary>Claude's plan mode may write its plan file under the user's .claude/plans folder.</summary>
    private static bool IsPlanFile(string path) =>
        path.Replace('/', '\\').Contains(@"\.claude\plans\", StringComparison.OrdinalIgnoreCase);

    private static List<string> FilePaths(JsonElement input)
    {
        var paths = new List<string>();
        if (input.ValueKind != JsonValueKind.Object)
        {
            return paths;
        }

        foreach (var name in new[] { "file_path", "notebook_path", "path" })
        {
            if (input.TryGetProperty(name, out var single) && single.ValueKind == JsonValueKind.String &&
                single.GetString() is { Length: > 0 } value)
            {
                paths.Add(value);
            }
        }

        if (input.TryGetProperty("paths", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            paths.AddRange(many.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)
                .Where(p => p.Length > 0));
        }

        return paths;
    }

    /// <summary>An edit inside the project (or a folder the user added) that is not repository or tool configuration.</summary>
    public static bool IsSafeToEdit(string path, string? cwd, IReadOnlyList<string>? extraRoots)
    {
        string full;
        try
        {
            full = Normalize(Path.IsPathRooted(path) || cwd is null ? path : Path.Combine(cwd, path));
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (full.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var roots = new List<string>();
        if (!string.IsNullOrEmpty(cwd))
        {
            roots.Add(Normalize(cwd));
        }

        if (extraRoots is not null)
        {
            roots.AddRange(extraRoots.Where(r => !string.IsNullOrEmpty(r)).Select(Normalize));
        }

        var root = roots.FirstOrDefault(r => full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                                              full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return false;
        }

        var relative = full.Length > root.Length ? full[(root.Length + 1)..] : "";
        return !relative.Split('\\').Any(part => ProtectedFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
    {
        var text = path.Replace('/', '\\').TrimEnd('\\');
        if (text.Length >= 2 && text[0] == '\\' && char.IsLetter(text[1]) && (text.Length == 2 || text[2] == '\\'))
        {
            text = $"{text[1]}:{text[2..]}";
        }

        return text;
    }
}
