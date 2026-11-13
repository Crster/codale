using System.Text.Json;

namespace Codale.Core.Agents;

/// <summary>
/// A shortcut the CLI offers alongside an approval request, e.g.
/// <c>{"type":"setMode","mode":"acceptEdits","destination":"session"}</c> or
/// <c>{"type":"addDirectories","directories":["X:\\repo\\src"],"destination":"session"}</c>.
/// Rendered as the extra buttons on the approval card; the accepted one is handed back
/// to the CLI verbatim (<see cref="Raw"/>) so it actually takes effect.
/// </summary>
public sealed record PermissionSuggestion
{
    public required string Type { get; init; }
    public string? Mode { get; init; }
    public string? Destination { get; init; }

    /// <summary>For addRules/replaceRules: allow, deny or ask.</summary>
    public string? Behavior { get; init; }

    /// <summary>For addDirectories/removeDirectories.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    /// <summary>For the rule types: each rule as "Tool" or "Tool(content)".</summary>
    public IReadOnlyList<(string Tool, string? Content)> Rules { get; init; } = [];

    /// <summary>The suggestion exactly as the CLI sent it, returned as an <c>updatedPermissions</c> entry.</summary>
    public JsonElement? Raw { get; init; }

    private bool ForSession => Destination is null or "session";

    /// <summary>A short button label, e.g. "Allow access to src" or "Always allow “npm test”".</summary>
    public string ToDisplayLabel() => Type switch
    {
        "setMode" => Mode switch
        {
            "acceptEdits" => ForSession ? "Allow all edits this session" : "Always allow edits",
            "bypassPermissions" => "Allow everything this session",
            "plan" => "Switch to plan mode",
            "default" => "Ask every time",
            { Length: > 0 } m => $"Switch to {m}",
            _ => "Change mode",
        },
        "addDirectories" => Directories.Count switch
        {
            1 => $"Allow access to {Shorten(FolderName(Directories[0]), 24)}",
            > 1 => $"Allow access to {Directories.Count} folders",
            _ => "Allow folder access",
        },
        "addRules" or "replaceRules" when Behavior is null or "allow" => Rules.Count switch
        {
            1 => (ForSession ? "Allow " : "Always allow ") + RuleLabel(Rules[0]) + (ForSession ? " this session" : ""),
            > 1 => ForSession ? $"Allow {Rules.Count} similar this session" : $"Always allow {Rules.Count} similar",
            _ => ForSession ? "Allow similar this session" : "Always allow similar",
        },
        "addRules" or "replaceRules" when Behavior == "deny" => Rules.Count == 1 ? $"Always deny {RuleLabel(Rules[0])}" : "Always deny similar",
        _ => Humanize(Type),
    };

    /// <summary>The full detail for a tooltip: every folder or rule, and where it is saved.</summary>
    public string ToDisplayDetail()
    {
        var where = Destination switch
        {
            null or "session" => "For this session only.",
            "localSettings" => "Saved to .claude/settings.local.json.",
            "projectSettings" => "Saved to .claude/settings.json.",
            "userSettings" => "Saved to your user settings.",
            var other => $"Saved to {other}.",
        };

        var what = Type switch
        {
            "addDirectories" when Directories.Count > 0 => "Also allows " + string.Join(", ", Directories) + ".",
            "addRules" or "replaceRules" when Rules.Count > 0 =>
                (Behavior == "deny" ? "Denies " : "Allows ") + string.Join(", ", Rules.Select(r => r.Content is { Length: > 0 } c ? $"{r.Tool}({c})" : r.Tool)) + ".",
            "setMode" when Mode is { Length: > 0 } => $"Switches the permission mode to {Mode}.",
            _ => "",
        };

        return string.Join(" ", new[] { what, where }.Where(s => s.Length > 0));
    }

    private static string RuleLabel((string Tool, string? Content) rule) =>
        rule.Content is { Length: > 0 } content
            ? $"“{Shorten(content.EndsWith(":*", StringComparison.Ordinal) ? content[..^2] : content, 28)}”"
            : rule.Tool;

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>"removeDirectories" → "Remove directories": never a raw identifier on a button.</summary>
    private static string Humanize(string type)
    {
        var words = new System.Text.StringBuilder();
        foreach (var c in type)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }

        return words.ToString();
    }
}
