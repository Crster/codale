using System.Text;
using System.Text.Json.Nodes;

using Codale.Commands;
using Codale.Core.Tasks;

namespace Codale.Mcp.Tasks;

/// <summary>
/// The <c>codale-tasks</c> tools: long-running commands that Codale itself runs, so the
/// user sees them in the session panel with live output and a Stop button.
/// </summary>
public static class TaskTools
{
    private static readonly object CommandsFileLock = new();

    /// <param name="tasks">Where started commands run.</param>
    /// <param name="projectRoot">
    /// The project whose command list the command tools read and write; null leaves those
    /// tools out (a server started without it has no project to talk about).
    /// </param>
    /// <param name="runDirectory">What a command's relative working directory resolves against; defaults to the project root.</param>
    /// <param name="assist">The app's background model; null leaves the explore tool out.</param>
    public static IReadOnlyList<McpTool> Create(
        ITaskService tasks, string? projectRoot = null, string? runDirectory = null, ITaskAssist? assist = null) =>
    [
        .. Core(tasks),
        .. string.IsNullOrWhiteSpace(projectRoot) ? [] : CommandTools(tasks, projectRoot, runDirectory ?? projectRoot),
        .. Panel(),
        .. assist is null ? [] : AssistTools(assist),
    ];

    /// <summary>
    /// Questions answered by Codale's background model instead of the agent's own reads, so
    /// the files it would have read never enter (and stay in) the chat model's context.
    /// </summary>
    private static IReadOnlyList<McpTool> AssistTools(ITaskAssist assist) =>
    [
        new McpTool(
            "explore",
            "Answer an open-ended question about this codebase (where is X handled, how does Y work, which files are involved) " +
            "with a separate, cheaper model that searches and reads the code for you. Returns a short answer with path:line ranges " +
            "and the key code. Use it before a chain of Grep/Glob/Read calls or an Explore subagent; then Read only the ranges you will change. " +
            "Usually under a minute; a hard question can take several. Wait for it rather than searching in parallel.",
            McpTool.Schema([("question", "string", "The question, specific and self-contained.")], "question"),
            async (a, ct) => await McpTool.Text(await Call(() => assist.ExploreAsync(a.RequiredString("question"), ct)))),
    ];

    /// <summary>
    /// The session panel's tools. They only validate and acknowledge: Codale reads the
    /// call itself from the agent's tool stream, so the panel is filled from a fixed
    /// schema of ours instead of the CLI's own task tools, whose shape varies by version.
    /// </summary>
    private static IReadOnlyList<McpTool> Panel() =>
    [
        new McpTool(
            "todos_set",
            "Record your task list in Codale's session panel. Send the WHOLE list every time (it replaces the previous one): " +
            "2-6 short concrete steps, at most one in_progress, finished ones completed. Call it when you start a piece of work, " +
            "when you begin each step and when you finish it. Use this instead of TodoWrite/TaskCreate.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["todos"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "The complete task list, in order.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "The step, imperative and short." },
                                ["status"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray("pending", "in_progress", "completed"),
                                },
                            },
                            ["required"] = new JsonArray("content", "status"),
                        },
                    },
                },
                ["required"] = new JsonArray("todos"),
            },
            (a, _) =>
            {
                var list = a.RequiredArray("todos");
                var done = list.Count(t => t.TryGetProperty("status", out var s) && s.GetString() == "completed");
                return McpTool.Text($"Task list updated: {done}/{list.Count} done.");
            }),

        new McpTool(
            "artifact_add",
            "Put something you produced in the Artifacts section of Codale's session panel so the user can open it: a report, " +
            "a document, an image or any file that is output rather than project source. Give a path to a file you wrote, or markdown " +
            "text to save as a document. Plans and browser screenshots are listed automatically; do not add them.",
            McpTool.Schema(
                [
                    ("title", "string", "Short name shown in the panel."),
                    ("path", "string", "Path of a file you already wrote."),
                    ("markdown", "string", "Markdown content to save as a document, when there is no file."),
                ],
                "title"),
            (a, _) =>
            {
                if (a.String("path") is null && a.String("markdown") is null)
                {
                    throw new McpToolException("Give either 'path' or 'markdown'.");
                }

                return McpTool.Text("Added to the session panel's artifacts.");
            }),
    ];

    private static IReadOnlyList<McpTool> CommandTools(ITaskService tasks, string projectRoot, string runDirectory) =>
    [
        new McpTool(
            "list_commands",
            "List the project's saved run commands (the list the user sees in Codale's commands menu): " +
            "Codale's own .codale/commands.json plus .claude/launch.json, VS Code tasks/launch and package.json scripts.",
            McpTool.Schema(),
            async (_, _) =>
            {
                var all = CommandCatalog.Discover(projectRoot);
                return await McpTool.Text(all.Count == 0
                    ? "No commands saved. Use add_command to save one."
                    : string.Join('\n', all.Select(c =>
                        $"{c.Name}  [{c.SourceLabel}]  {c.Command}{(c.WorkingDirectory is { } d ? $"  (in {d})" : "")}")));
            }),

        new McpTool(
            "add_command",
            "Save a run command to the project's list (.codale/commands.json) so it appears in Codale's commands menu " +
            "for the user. An existing command with the same name is replaced. It only saves; use run_command to run it.",
            McpTool.Schema(
                [
                    ("name", "string", "Short label shown in the menu, e.g. \"dev server\"."),
                    ("command", "string", "The command line to run (PowerShell syntax on Windows)."),
                    ("cwd", "string", "Optional working directory, relative to the project root."),
                ],
                "name",
                "command"),
            async (a, _) =>
            {
                try
                {
                    var (name, command, cwd) = (a.RequiredString("name"), a.RequiredString("command"), a.String("cwd"));
                    ProjectCommand saved;
                    lock (CommandsFileLock)
                    {
                        // Calls run concurrently and Add rewrites the whole file: take turns.
                        saved = CommandCatalog.Add(projectRoot, name, command, cwd);
                    }

                    return await McpTool.Text($"Saved \"{saved.Name}\" to {saved.SourcePath}.");
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    throw new McpToolException(e.Message);
                }
            }),

        new McpTool(
            "run_command",
            "Run a saved command from the project's list by name, as a Codale task: the user sees it live and can stop it, " +
            "and you read it with read_task / stop_task like any start_task. Returns its id and the first output.",
            McpTool.Schema(
                [
                    ("name", "string", "The command's name, as list_commands shows it."),
                    ("wait_seconds", "integer", "How long to wait for early output before returning (default 3, max 60)."),
                ],
                "name"),
            async (a, ct) =>
            {
                var command = Resolve(CommandCatalog.Discover(projectRoot), a.RequiredString("name"));
                return Format(await Call(() => tasks.StartAsync(
                    InDirectory(command, runDirectory), command.Name, a.Int("wait_seconds") ?? 3, ct)));
            }),
    ];

    /// <summary>Exact name first (case-insensitive), then a unique substring; anything else names the candidates.</summary>
    private static ProjectCommand Resolve(IReadOnlyList<ProjectCommand> all, string name)
    {
        name = name.Trim();
        if (all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        var partial = all.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return partial[0];
        }

        var available = string.Join(", ", (partial.Count > 1 ? partial : all).Select(c => $"\"{c.Name}\""));
        throw new McpToolException(partial.Count > 1
            ? $"\"{name}\" matches several commands: {available}."
            : all.Count == 0
                ? "No commands saved. Use add_command to save one."
                : $"No command named \"{name}\". Available: {available}.");
    }

    /// <summary>The command line, prefixed with a directory change when the entry names a working directory.</summary>
    private static string InDirectory(ProjectCommand command, string runDirectory)
    {
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return command.Command;
        }

        var dir = Path.GetFullPath(Path.Combine(runDirectory, command.WorkingDirectory));
        return OperatingSystem.IsWindows()
            ? $"Set-Location -LiteralPath '{dir.Replace("'", "''")}'; {command.Command}"
            : $"cd '{dir.Replace("'", "'\\''")}' && {command.Command}";
    }

    private static IReadOnlyList<McpTool> Core(ITaskService tasks) =>
    [
        new McpTool(
            "start_task",
            "Start a long-running command (dev server, file watcher, build in watch mode, anything that keeps running) " +
            "in the background, owned by Codale. Returns its id and the first output. The user sees it live and can stop it. " +
            "Use this instead of a shell's background option, which is disabled.",
            McpTool.Schema(
                [
                    ("command", "string", "The command line to run (PowerShell syntax on Windows), in the project directory."),
                    ("name", "string", "Optional short label shown to the user, e.g. \"dev server\"."),
                    ("wait_seconds", "integer", "How long to wait for early output before returning (default 3, max 60)."),
                ],
                "command"),
            async (a, ct) => Format(await Call(() => tasks.StartAsync(
                a.RequiredString("command"), a.String("name"), a.Int("wait_seconds") ?? 3, ct)))),

        new McpTool(
            "read_task",
            "Read a task's output and state. Pass the next_offset from a previous read as 'since' to get only new output.",
            McpTool.Schema(
                [
                    ("id", "string", "The task id from start_task."),
                    ("since", "integer", "Only output after this offset (the next_offset of an earlier read)."),
                    ("tail_chars", "integer", "Cap the reply to the last N characters (default 8000)."),
                ],
                "id"),
            async (a, ct) =>
            {
                var since = a.Int("since");
                return Format(await Call(() => tasks.ReadAsync(a.RequiredString("id"), since, a.Int("tail_chars"), ct)));
            }),

        new McpTool(
            "list_tasks",
            "List this session's tasks with their state.",
            McpTool.Schema(),
            async (_, ct) =>
            {
                var all = await Call(() => tasks.ListAsync(ct));
                return await McpTool.Text(all.Count == 0
                    ? "No tasks."
                    : string.Join('\n', all.Select(t => $"{t.Id}  {t.State}{(t.ExitCode is { } c ? $" (exit {c})" : "")}  {t.Name}")));
            }),

        new McpTool(
            "stop_task",
            "Stop a task and everything it started.",
            McpTool.Schema([("id", "string", "The task id from start_task.")], "id"),
            async (a, ct) => Format(await Call(() => tasks.StopAsync(a.RequiredString("id"), ct)))),
    ];

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TaskServiceException e)
        {
            throw new McpToolException(e.Message);
        }
    }

    private static IReadOnlyList<McpContent> Format(TaskSnapshot t)
    {
        var text = new StringBuilder()
            .Append("task ").Append(t.Id).Append(" (").Append(t.Name).Append("): ").Append(t.State);

        if (t.ExitCode is { } code)
        {
            text.Append(", exit code ").Append(code);
        }

        text.Append("\nnext_offset: ").Append(t.NextOffset);
        text.Append(t.Output.Length == 0 ? "\n(no output yet)" : "\n--- output ---\n" + t.Output.TrimEnd());
        return [McpContent.FromText(text.ToString())];
    }
}
