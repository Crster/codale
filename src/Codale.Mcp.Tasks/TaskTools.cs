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
            "Ask a question about THIS project's source code and get back the answer plus the exact files and line ranges that hold it. " +
            "A separate helper model does the searching and reading, so nothing it reads enters your context. " +
            "Use it for any question you cannot answer from a file you already have open: where is X implemented, how does Y work end to end, " +
            "which files are involved in Z, what calls W, how is feature F wired up. Call it BEFORE a chain of Grep/Glob/Read calls or an Explore subagent. " +
            "Do not use it when you already know the file or symbol (Grep/Read that directly), for files outside the project, or for general programming questions. " +
            "Reply: a direct answer, then 'Files to read' with one line per file as path:start-end, what that range declares (types, methods, properties) " +
            "and the words to grep for inside it. Read only the ranges it names. Takes 10-60 seconds; wait for it rather than searching in parallel.",
            McpTool.Schema([("question", "string", "One specific, self-contained question about this project's code, e.g. \"How does the editor save a file and which service writes it?\" Include the names you already know.")], "question"),
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
            "Replace the step list shown in Codale's session panel, the user's progress view of this chat. This is the ONLY way to show " +
            "progress: the built-in TodoWrite/TaskCreate/TaskUpdate tools are disabled here. Pass the COMPLETE list every call, not just " +
            "the changed item: 2-6 short imperative steps, at most one in_progress, finished steps completed. Call it when you start a piece " +
            "of work, each time you move to the next step, and when the last step finishes. Not for notes, questions or results; the panel shows steps only.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["todos"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "The complete step list, in order; it replaces whatever was shown before.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "The step, imperative and short, e.g. \"Add the HexViewer control\"." },
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
            "List a deliverable you produced in the Artifacts section of Codale's session panel so the user can open it with one click. " +
            "Only for output that is not project source: a report, an analysis, a generated document, an exported image, a data file. " +
            "Pass path for a file you already wrote, or markdown to have Codale save the text as a document. " +
            "Do not add source files you edited, plans, or browser screenshots: those are listed automatically.",
            McpTool.Schema(
                [
                    ("title", "string", "Short name shown in the panel, e.g. \"Dependency audit\"."),
                    ("path", "string", "Path of a file you already wrote. Give this or markdown."),
                    ("markdown", "string", "Markdown text to save as a document when there is no file. Give this or path."),
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
            "Show the project's saved run commands: the entries in Codale's commands menu, read from .codale/commands.json, " +
            ".claude/launch.json, VS Code tasks.json/launch.json and package.json scripts. One line each: name, source, command line. " +
            "Read-only. Call it before run_command to get the exact name, or before add_command to avoid saving a duplicate.",
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
            "Save a reusable run command (build, test, dev server, lint...) to the project's commands menu in .codale/commands.json " +
            "so the user can run it later with one click. It only saves; nothing runs. Use run_command to run it afterwards. " +
            "Saving a name that already exists replaces that entry. Not for one-off commands: run those with the shell tool or start_task.",
            McpTool.Schema(
                [
                    ("name", "string", "Short label shown in the menu, e.g. \"dev server\" or \"run tests\"."),
                    ("command", "string", "The command line to run (PowerShell syntax on Windows), e.g. \"npm run dev\"."),
                    ("cwd", "string", "Optional working directory, relative to the project root. Omit to run at the root."),
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
            "Run one of the project's saved commands (see list_commands) by name, in the background as a Codale task: the user watches " +
            "its output live in the session panel and can stop it. Returns the task id and the first seconds of output; call read_task " +
            "to follow it and stop_task to end it. Use this for a saved command; use start_task for an arbitrary command line.",
            McpTool.Schema(
                [
                    ("name", "string", "The command's name exactly as list_commands shows it; a unique partial name also works."),
                    ("wait_seconds", "integer", "Seconds to wait for early output before returning (default 3, max 60). Raise it for a server that takes a while to print its URL."),
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
            "Run a command line in the background as a Codale task, for anything that keeps running or takes long: a dev server, " +
            "a file watcher, a build in watch mode, a long test suite. The user sees it live in the session panel and can stop it. " +
            "Returns the task id and the first seconds of output; then read_task to follow it and stop_task when you are done with it. " +
            "The shell tools' background modes (run_in_background, &, Start-Job, nohup) are blocked here: use this instead. " +
            "A quick command that finishes in seconds still belongs in the normal shell tool.",
            McpTool.Schema(
                [
                    ("command", "string", "The command line to run (PowerShell syntax on Windows), started in the project directory, e.g. \"npm run dev\"."),
                    ("name", "string", "Optional short label shown to the user, e.g. \"dev server\"."),
                    ("wait_seconds", "integer", "Seconds to wait for early output before returning (default 3, max 60). Raise it for a server that takes a while to print its URL."),
                ],
                "command"),
            async (a, ct) => Format(await Call(() => tasks.StartAsync(
                a.RequiredString("command"), a.String("name"), a.Int("wait_seconds") ?? 3, ct)))),

        new McpTool(
            "read_task",
            "Read the output and state (running or exited, with exit code) of a task started by start_task or run_command. " +
            "Use it to check that a server is up, a build finished, or an error appeared. Pass the next_offset from the previous reply " +
            "as since to get only the output that arrived after it; tail_chars caps the size of the reply.",
            McpTool.Schema(
                [
                    ("id", "string", "The task id returned by start_task or run_command (list_tasks shows them)."),
                    ("since", "integer", "Return only output after this offset: the next_offset from the previous read of this task."),
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
            "List every task started in this chat with its id, state (running or exited) and exit code. " +
            "Use it to find a task id you no longer have, or to see what is still running before starting another on the same port.",
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
            "Stop a running task and every process it spawned. Use it when the user is done with a server or watcher you started, " +
            "and before starting a replacement that needs the same port.",
            McpTool.Schema([("id", "string", "The task id returned by start_task or run_command (list_tasks shows them).")], "id"),
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
