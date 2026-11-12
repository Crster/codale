using System.Text;
using System.Text.Json.Nodes;

using Codale.Commands;
using Codale.Core.Serving;
using Codale.Core.Tasks;
using Codale.Workspace;

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
    public static IReadOnlyList<McpTool> Create(
        ITaskService tasks, string? projectRoot = null, string? runDirectory = null) =>
    [
        .. Core(tasks),
        .. ServeTools(tasks, runDirectory ?? projectRoot),
        .. string.IsNullOrWhiteSpace(projectRoot) ? [] : CommandTools(tasks, projectRoot, runDirectory ?? projectRoot),
        .. string.IsNullOrWhiteSpace(projectRoot) ? [] : WorkspaceTools(projectRoot),
        .. Panel(tasks),
    ];

    /// <summary>
    /// The session panel's tools. They only validate and acknowledge: Codale reads the
    /// call itself from the agent's tool stream, so the panel is filled from a fixed
    /// schema of ours instead of the CLI's own task tools, whose shape varies by version.
    /// </summary>
    /// <summary>
    /// Set by the host in the environment of a server handed to an agent CLI running in a terminal.
    /// Codale cannot read such a CLI's tool stream, so its panel calls are sent over the pipe instead.
    /// </summary>
    private static bool PushesToPanel(ITaskService tasks) =>
        tasks is TaskPipeClient && Environment.GetEnvironmentVariable("CODALE_PANEL_PUSH") == "1";

    /// <summary>
    /// Sends a panel call to the app when this server runs for a terminal CLI; null when it was
    /// delivered (or is not pushed at all), otherwise why not. Each attempt is logged to
    /// <c>panel-push.log</c> beside Codale's other data, since a CLI's tool output is easy to miss.
    /// </summary>
    private static async Task<string?> PublishAsync(ITaskService tasks, string kind, System.Text.Json.JsonElement input, CancellationToken ct)
    {
        if (!PushesToPanel(tasks))
        {
            Log($"{kind}: not pushed (pipe client = {tasks is TaskPipeClient}, CODALE_PANEL_PUSH = {Environment.GetEnvironmentVariable("CODALE_PANEL_PUSH")}) input = {input.GetRawText()}");
            return null;
        }

        var failure = await ((TaskPipeClient)tasks).PublishPanelAsync(kind, input, ct);
        Log($"{kind}: {(failure is null ? "delivered" : "FAILED: " + failure)} input = {input.GetRawText()}");
        return failure;
    }

    private static void Log(string line)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "panel-push.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<McpTool> Panel(ITaskService tasks) =>
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
            async (a, ct) =>
            {
                var list = a.RequiredArray("todos");
                var done = list.Count(t => t.TryGetProperty("status", out var s) && s.GetString() == "completed");
                var failure = await PublishAsync(tasks, "todos", a.Raw, ct);
                return await McpTool.Text(failure is null
                    ? $"Task list updated: {done}/{list.Count} done."
                    : $"Task list NOT shown in Codale's panel: {failure}");
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
            async (a, ct) =>
            {
                if (a.String("path") is null && a.String("markdown") is null)
                {
                    throw new McpToolException("Give either 'path' or 'markdown'.");
                }

                var failure = await PublishAsync(tasks, "artifact", a.Raw, ct);
                return await McpTool.Text(failure is null
                    ? "Added to the session panel's artifacts."
                    : $"NOT added to Codale's panel: {failure}");
            }),
    ];

    private static IReadOnlyList<McpTool> CommandTools(ITaskService tasks, string projectRoot, string runDirectory) =>
    [
        new McpTool(
            "list_commands",
            "Show the project's saved and auto-detected run commands: the entries in Codale's commands menu, read from .codale/commands.json, " +
            ".claude/launch.json, VS Code tasks.json/launch.json, package.json scripts, and detected project files (.NET, Gradle, Maven, Python, Cargo, Go, Composer, Laravel, Makefile, Justfile, Taskfile, Docker Compose, Prisma, etc.). One line each: name, source, command line. " +
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

    /// <summary>
    /// The workspace database's memory and secrets. The database is Codale's own, outside the project, and one
    /// process at a time opens it, so a busy answer is reported rather than waited on forever.
    /// </summary>
    private static IReadOnlyList<McpTool> WorkspaceTools(string projectRoot)
    {
        var directory = WorkspaceDb.DirectoryFor(projectRoot);
        var memory = new MemoryStore(directory);
        var env = new EnvStore(directory);

        static async Task<IReadOnlyList<McpContent>> Guarded(Func<string> work)
        {
            try
            {
                return await McpTool.Text(work());
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new McpToolException(e.Message);
            }
        }

        static string Show(MemoryNote note) =>
            $"[{note.Id}] {note.Title}{(note.Tags.Count > 0 ? $"  ({string.Join(", ", note.Tags)})" : "")}\n{note.Text}";

        return
        [
            new McpTool(
                "memory_save",
                "Remember something about this workspace for later conversations: a decision and why, a convention, where something lives, " +
                "how to build or test, a preference the user stated. Keep it short and self-contained. Saving under a title that already exists " +
                "replaces that note, so correct a stale memory by saving it again. Not for the current task's progress or anything the code already says.",
                McpTool.Schema(
                    [
                        ("title", "string", "A short name that identifies the note, e.g. \"Test command\"."),
                        ("text", "string", "What to remember, a few sentences at most."),
                        ("tags", "string", "Optional comma-separated tags, e.g. \"build, ci\"."),
                    ],
                    "title",
                    "text"),
                (a, _) => Guarded(() =>
                {
                    var note = memory.Save(a.RequiredString("title"), a.RequiredString("text"),
                        a.String("tags")?.Split(',') ?? []);
                    return $"Remembered as [{note.Id}].";
                })),

            new McpTool(
                "memory_search",
                "Look up what was remembered about this workspace (memory_save). Pass a question or keywords. Check it at the start of a task " +
                "that depends on project conventions, and before asking the user something they may already have told you.",
                McpTool.Schema(
                    [
                        ("query", "string", "What you want to recall."),
                        ("limit", "integer", "How many notes at most (default 5)."),
                    ],
                    "query"),
                (a, _) => Guarded(() =>
                {
                    var found = memory.Search(a.RequiredString("query"), Math.Clamp(a.Int("limit") ?? 5, 1, 20));
                    return found.Count == 0 ? "Nothing remembered about that." : string.Join("\n\n", found.Select(Show));
                })),

            new McpTool(
                "memory_list",
                "List every note remembered about this workspace, newest first: id, title and tags, one line each.",
                McpTool.Schema(),
                (_, _) => Guarded(() =>
                {
                    var all = memory.List();
                    return all.Count == 0
                        ? "Nothing remembered yet."
                        : string.Join('\n', all.Select(n => $"[{n.Id}] {n.Title}{(n.Tags.Count > 0 ? $"  ({string.Join(", ", n.Tags)})" : "")}"));
                })),

            new McpTool(
                "memory_forget",
                "Delete a remembered note that is wrong or no longer true. Use the id memory_list or memory_search shows.",
                McpTool.Schema([("id", "string", "The note's id.")], "id"),
                (a, _) => Guarded(() => memory.Forget(a.RequiredString("id")) ? "Forgotten." : "No such note.")),

            new McpTool(
                "env_list",
                "List the names of the secrets stored for this workspace (API keys, tokens, connection strings). Never shows values.",
                McpTool.Schema(),
                (_, _) => Guarded(() =>
                {
                    var names = env.Names();
                    return names.Count == 0 ? "No secrets stored." : string.Join('\n', names);
                })),

            new McpTool(
                "env_get",
                "Read one stored secret's value, to use in a command or request you are running for the user. The value is sensitive: " +
                "use it where it is needed, never write it into files, code, commits or messages, and do not repeat it back.",
                McpTool.Schema([("name", "string", "The secret's name as env_list shows it.")], "name"),
                (a, _) => Guarded(() => env.Get(a.RequiredString("name")) ?? throw new McpToolException("No secret by that name. env_list shows the names."))),

            new McpTool(
                "env_set",
                "Store a secret the user gave you and asked you to keep (name and value). It is encrypted for this Windows account. " +
                "Do not invent values or store anything the user did not ask you to.",
                McpTool.Schema(
                    [
                        ("name", "string", "Letters, digits and underscores, e.g. STRIPE_API_KEY."),
                        ("value", "string", "The secret."),
                    ],
                    "name",
                    "value"),
                (a, _) => Guarded(() =>
                {
                    env.Set(a.RequiredString("name"), a.RequiredString("value"));
                    return $"Stored {a.RequiredString("name")}.";
                })),
        ];
    }

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
        return $"Set-Location -LiteralPath '{dir.Replace("'", "''")}'; {command.Command}";
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
            "list_terminals",
            "List the terminal tabs the USER has open in Codale (plain shells and commands run from the commands menu), with id, state " +
            "(running or exited), exit code and the command line. Use it when the user says a command in their terminal failed or asks " +
            "you to look at it; then read_terminal to diagnose the output. Read-only: you cannot type into these terminals.",
            McpTool.Schema(),
            async (_, ct) =>
            {
                var all = await Call(() => tasks.ListTerminalsAsync(ct));
                return await McpTool.Text(all.Count == 0
                    ? "No terminals open."
                    : string.Join('\n', all.Select(t => $"{t.Id}  {t.State}{(t.ExitCode is { } c ? $" (exit {c})" : "")}  {t.Name}")));
            }),

        new McpTool(
            "read_terminal",
            "Read what one of the user's terminal tabs printed, as plain text, with its state and exit code. Use it to diagnose a failed " +
            "build, test run or server the user ran themselves. Pass the next_offset from the previous reply as since to get only newer " +
            "output; tail_chars caps the size of the reply (default 8000, the end of the output is what you usually want).",
            McpTool.Schema(
                [
                    ("id", "string", "The terminal id from list_terminals."),
                    ("since", "integer", "Return only output after this offset: the next_offset from the previous read of this terminal."),
                    ("tail_chars", "integer", "Cap the reply to the last N characters (default 8000)."),
                ],
                "id"),
            async (a, ct) =>
            {
                var since = a.Int("since");
                return Format(await Call(() => tasks.ReadTerminalAsync(a.RequiredString("id"), since, a.Int("tail_chars"), ct)));
            }),

        new McpTool(
            "stop_task",
            "Stop a running task and every process it spawned. Use it when the user is done with a server or watcher you started, " +
            "and before starting a replacement that needs the same port.",
            McpTool.Schema([("id", "string", "The task id returned by start_task or run_command (list_tasks shows them).")], "id"),
            async (a, ct) => Format(await Call(() => tasks.StopAsync(a.RequiredString("id"), ct)))),
    ];

    /// <summary>A localhost static server for any folder, so built output can be looked at in a browser.</summary>
    private static IReadOnlyList<McpTool> ServeTools(ITaskService tasks, string? baseDirectory) =>
    [
        new McpTool(
            "serve_folder",
            "Serve a folder as a static website on http://localhost:PORT/ so it can be opened in a browser: a built site (dist, build, " +
            "out), an HTML report, a coverage or docs folder, a page you generated. Codale hosts it (no command or task needed); the user " +
            "sees it in the Agent tools menu and it stays up until stop_serving or Codale closes. Serving the same folder again returns the " +
            "same URL. Folders show index.html, otherwise a file listing. Use this instead of starting an http server with start_task; " +
            "for a project with its own dev server (npm run dev) use start_task.",
            McpTool.Schema(
                [
                    ("path", "string", "The folder to serve: absolute, or relative to the project directory, e.g. \"dist\"."),
                    ("port", "integer", "Optional fixed port; by default a free one is picked."),
                    ("spa", "boolean", "Answer unknown extensionless paths with index.html, for a single-page app with client-side routes (default false)."),
                ],
                "path"),
            async (a, ct) =>
            {
                var folder = ResolveFolder(a.RequiredString("path"), baseDirectory);
                var served = await Call(() => tasks.ServeFolderAsync(folder, a.Int("port"), a.Bool("spa") ?? false, ct));
                return await McpTool.Text(
                    $"Serving {served.Folder} at {served.Url} (id {served.Id}). It stays up until stop_serving or Codale closes.");
            }),

        new McpTool(
            "list_served",
            "List the folders Codale is serving on localhost, with id, URL and folder, whether you or the user started them.",
            McpTool.Schema(),
            async (_, ct) =>
            {
                var all = await Call(() => tasks.ListServedAsync(ct));
                return await McpTool.Text(all.Count == 0
                    ? "No folders are being served."
                    : string.Join('\n', all.Select(Describe)));
            }),

        new McpTool(
            "stop_serving",
            "Stop serving a folder started with serve_folder (or by the user), freeing its port.",
            McpTool.Schema([("id", "string", "The id from serve_folder or list_served (a URL or folder path works too).")], "id"),
            async (a, ct) =>
            {
                var stopped = await Call(() => tasks.StopServingAsync(a.RequiredString("id"), ct));
                return await McpTool.Text($"Stopped {stopped.Id}: {stopped.Url} no longer serves {stopped.Folder}.");
            }),
    ];

    private static string Describe(ServedFolder s) => $"{s.Id}  {s.Url}  {s.Folder}  (started by {s.StartedBy})";

    private static string ResolveFolder(string path, string? baseDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return expanded;
        }

        return string.IsNullOrWhiteSpace(baseDirectory)
            ? throw new McpToolException("Pass an absolute folder path; there is no project directory to resolve a relative one against.")
            : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

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