using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Core.Agents;

/// <summary>
/// Recognises long-running work in ordinary tool calls: shells started with
/// <c>run_in_background</c> and subagents. Pure, so the wording it depends on is
/// covered by unit tests rather than trusted.
/// </summary>
public static partial class BackgroundTaskDetector
{
    public static bool IsSubagentTool(string toolName) => toolName is "Task" or "Agent";

    /// <summary>Codale's helper-model tools (codale-tasks explore) work like a subagent.</summary>
    public static bool IsHelperTool(string toolName) =>
        toolName.Contains("codale", StringComparison.OrdinalIgnoreCase)
        && toolName.EndsWith("__explore", StringComparison.Ordinal);

    public static bool IsShellTool(string toolName) => toolName is "Bash" or "PowerShell" or "Shell";

    /// <summary>True when a shell call asked to run in the background.</summary>
    public static bool IsBackgroundShell(string toolName, JsonElement input) =>
        IsShellTool(toolName)
        && input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("run_in_background", out var flag)
        && flag.ValueKind == JsonValueKind.True;

    /// <summary>
    /// True when a shell call detaches from the turn: <c>run_in_background</c>, or a
    /// <c>mode: "async"</c> / <c>detach</c> flag.
    /// This is what the host refuses, so long-running work goes through its own task tools.
    /// </summary>
    public static bool IsBackgroundLaunch(string toolName, JsonElement input)
    {
        if (!IsShellTool(toolName) || input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return IsBackgroundShell(toolName, input)
            || (input.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String
                && string.Equals(mode.GetString(), "async", StringComparison.OrdinalIgnoreCase))
            || (input.TryGetProperty("detach", out var detach) && detach.ValueKind == JsonValueKind.True);
    }

    /// <summary>The refusal the model reads when it tries to background a shell itself.</summary>
    public const string BackgroundBlockedMessage =
        "Background shells are disabled in Codale. To run a dev server, watcher or any long-running command, " +
        "call the codale-tasks start_task tool (mcp__codale-tasks__start_task) instead, then read_task to check output and stop_task to end it. " +
        "Codale shows the task and its live output to the user. Short commands can still run normally in the foreground.";

    /// <summary>The shell id a <c>BashOutput</c>/<c>KillShell</c> call addresses, if any.</summary>
    public static string? ShellIdOf(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "bash_id", "shell_id", "task_id" })
        {
            if (input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }

    /// <summary>Pulls the shell id and output file out of a background launch's result text.</summary>
    public static (string? ShellId, string? OutputPath) ParseLaunchResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return (null, null);
        }

        var id = IdPattern().Match(resultText);
        var path = PathPattern().Match(resultText);
        return (
            id.Success ? id.Groups[1].Value : null,
            path.Success ? path.Groups[1].Value.Trim().TrimEnd('.', '"', '\'') : null);
    }

    /// <summary>Strips ANSI escape sequences so a dev server's colours don't render as noise.</summary>
    public static string StripAnsi(string text) => AnsiPattern().Replace(text, "");

    // "with ID: bx7k2m."
    [GeneratedRegex(@"(?:\bshell)?\bID:?\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"(?:written to|output file|output is at|saved to):?\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiPattern();
}
