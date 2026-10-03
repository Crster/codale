using System.Text.Json.Nodes;

namespace Codale.Core.Agents;

/// <summary>
/// Replacements for Claude Code's built-in Explore and general-purpose subagents, passed
/// with <c>--agents</c>. The built-ins run on the chat's own model with its thinking, so a
/// review that fans out to eight of them pays top price for every file read. Same names
/// and descriptions, so the agent picks them as before, but on a cheaper model, told to
/// read in ranges and to report back briefly: what a subagent returns lands in the
/// parent's context too.
/// </summary>
public static class ClaudeSubagents
{
    public const string ExploreModel = "haiku";
    public const string WorkerModel = "sonnet";
    public const string WorkerEffort = "medium";

    /// <summary>The <c>--agents</c> document.</summary>
    /// <param name="mainModel">The chat's model, when known: a chat already on Haiku keeps its workers on it.</param>
    /// <param name="assist">Whether the explore tool exists, so the prompts can send the subagents to them.</param>
    public static string Build(string? mainModel, bool assist)
    {
        var workerModel = mainModel is { } m && m.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? "inherit" : WorkerModel;

        var explore = new JsonObject
        {
            ["description"] =
                "Fast, read-only agent for exploring a codebase: finding files, searching code and answering questions about how " +
                "code works. Say how thorough to be: quick, medium or very thorough.",
            ["prompt"] = ExplorePrompt(assist),
            ["model"] = ExploreModel,
            ["disallowedTools"] = new JsonArray("Edit", "Write", "NotebookEdit", "Agent", "Task"),
        };

        var worker = new JsonObject
        {
            ["description"] =
                "General-purpose agent for researching complex questions, searching for code and carrying out multi-step tasks, " +
                "including changing code.",
            ["prompt"] = WorkerPrompt(assist),
            ["model"] = workerModel,
            ["effort"] = WorkerEffort,
        };

        return new JsonObject { ["Explore"] = explore, ["general-purpose"] = worker }.ToJsonString();
    }

    private static string AssistLine(bool assist) => assist
        ? "For an open-ended question (where is X, how does Y work) call mcp__codale-tasks__explore first; it runs on a separate model and costs you almost nothing. " +
          "If it is not directly available, load it with one ToolSearch (select:mcp__codale-tasks__explore). "
        : "";

    private const string ReadEconomy =
        "Locate code with Grep and Glob, then Read only the line ranges you need (offset/limit). Do not read a file you already " +
        "read, and never print whole files through the shell (cat, type, Get-Content). ";

    private static string ExplorePrompt(bool assist) =>
        "You are a read-only codebase explorer working for another agent. Everything you read stays in your context and is paid " +
        "for again on every step, and your report goes into the other agent's context, so be economical. " +
        AssistLine(assist) + ReadEconomy +
        "Do not change any file. Report: the direct answer first, then the evidence as path:line with one line each. Quote code only " +
        "when its exact text matters, at most ten lines a quote. Stay under 400 words unless asked for more.";

    private static string WorkerPrompt(bool assist) =>
        "You are a software engineering agent finishing a task another agent handed you. Do the whole task, carefully and correctly. " +
        "Work economically: everything you read stays in your context for every later step. " +
        AssistLine(assist) + ReadEconomy +
        "Check your change with the narrowest build or test that covers it. Finish with a short report: what you changed (path:line), " +
        "what you verified and how, and anything left open. Do not paste code back unless asked.";
}
