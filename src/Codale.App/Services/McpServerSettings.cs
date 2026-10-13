using Codale.Core.Agents;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// Which of Codale's built-in MCP servers a project's agents get: a browser the agent
/// can drive to test and verify what it built, and desktop control (screenshots, mouse,
/// keyboard) for everything a browser cannot reach. The browser is a global preference
/// that defaults on; desktop control acts on the real screen, so it is per project and
/// defaults off. Like <see cref="CliEndpointSettings"/> the values are re-read on every
/// access, so a toggle applies to the next session start.
/// </summary>
public sealed class McpServerSettings
{
    public const string BrowserName = "codale-browser";
    public const string ComputerName = "codale-computer";

    private const string BrowserKey = "mcp.browser.enabled";
    private const string ComputerKey = "mcp.computer.enabled";

    private readonly CodaleStore _store;
    private readonly string _projectPath;
    private readonly string _baseDirectory;

    public McpServerSettings(CodaleStore store, string projectPath, string? baseDirectory = null)
    {
        _store = store;
        _projectPath = projectPath;
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// The servers for an agent CLI running in a terminal, supplied by the workspace so they include
    /// the tasks server wired to the panel's chat. Unset, the servers started now are used.
    /// </summary>
    public Func<IReadOnlyList<McpServerSpec>>? TerminalServersProvider { get; set; }

    private IReadOnlyList<McpServerSpec> TerminalServers() => TerminalServersProvider?.Invoke() ?? Servers();

    /// <summary>On unless the user turned it off.</summary>
    public bool BrowserEnabled
    {
        get => _store.GetSetting(BrowserKey) != "0";
        set => _store.SetSetting(BrowserKey, value ? "1" : "0");
    }

    /// <summary>Off unless the user turned it on for this project.</summary>
    public bool ComputerEnabled
    {
        get => _store.GetUiState(_projectPath, ComputerKey) == "1";
        set => _store.SetUiState(_projectPath, ComputerKey, value ? "1" : "0");
    }

    public bool BrowserAvailable => File.Exists(ExePath("Codale.Mcp.Browser"));

    public bool ComputerAvailable => File.Exists(ExePath("Codale.Mcp.Computer"));

    public const string TasksName = "codale-tasks";

    /// <summary>
    /// Long-running commands are run by Codale itself, so the tasks server ships with
    /// the app and is not a user preference: without it the agent's own background
    /// shells stay allowed, because blocking them would leave no way to run a dev server.
    /// </summary>
    public bool TasksAvailable => File.Exists(ExePath("Codale.Mcp.Tasks"));

    /// <summary>The tasks server, wired to this chat's pipe; the exe finds the app through its environment.</summary>
    /// <param name="runDirectory">Where the chat's tasks run (its worktree, when it has one); the command list itself is always the project's.</param>
    /// <param name="assist">Offer the explore tool, answered by the background-task model.</param>
    /// <param name="pushToPanel">For an agent CLI in a terminal, whose tool stream the app cannot read: todos_set and artifact_add are sent over the pipe.</param>
    public McpServerSpec? TasksSpec(string pipeName, string token, string? runDirectory = null, bool assist = false, bool pushToPanel = false) => !TasksAvailable
        ? null
        : new McpServerSpec
        {
            Name = TasksName,
            Command = RunnablePath("Codale.Mcp.Tasks"),

            // Codale's own server (task list, artifacts, background tasks): no approval prompt for it.
            AutoApprove = true,

            // Starting processes and saving commands reach beyond the sandbox, so these still ask.
            AskTools = ["start_task", "run_command", "add_command"],
            Env = new Dictionary<string, string>
            {
                ["CODALE_TASKS_PIPE"] = pipeName,
                ["CODALE_TASKS_TOKEN"] = token,
                ["CODALE_PROJECT_DIR"] = _projectPath,
                ["CODALE_RUN_DIR"] = runDirectory ?? _projectPath,
                ["CODALE_ASSIST"] = assist ? "1" : "0",
                ["CODALE_PANEL_PUSH"] = pushToPanel ? "1" : "0",
            },
        };

    /// <summary>
    /// The <c>PostToolUse</c> hook that condenses long shell output; with
    /// <paramref name="digest"/> it asks the background-task model before cutting.
    /// </summary>
    public string? ShellOutputHookCommand(bool digest) => !TasksAvailable
        ? null
        : TasksHook($"--posttooluse-hook{(digest ? " --digest" : "")}");

    /// <summary>The <c>PreToolUse</c> hook that turns away a first whole-file read of a large file.</summary>
    public string? ReadGuardCommand() => !TasksAvailable
        ? null
        : TasksHook("--pretooluse-hook --read-guard 400");

    /// <summary>Standing instruction that sends exploration to the background-task model.</summary>
    public const string AssistDirective =
        "To save context, codebase exploration goes through Codale's helper model: for any question about this project's code " +
        "that you cannot answer from a file you already have open (where is X handled, how does Y work, which files are involved in Z) " +
        "call mcp__codale-tasks__explore FIRST, instead of a chain of Grep/Glob/Read calls or an Explore/Task subagent. It answers " +
        "with the files to read as path:lines and what each range declares; then Read only those ranges. When you already know the " +
        "file or symbol, Grep and Read it directly, including large files when you need them. The tool is deferred: load it first " +
        "with one ToolSearch (select:mcp__codale-tasks__explore).";

    /// <summary>Standing instruction for short replies, when the user turned it on.</summary>
    public const string TerseDirective =
        "Keep replies short: no preamble and no recap of what you are about to do or just did. Do not reprint code you " +
        "wrote or read; point to it as path:line. Report results in at most five bullets unless the user asks for more.";

    /// <summary>The CLI's own task-list tools, switched off when todos_set is available so the panel has one source.</summary>
    public const string NativeTodoTools = "TodoWrite,TaskCreate,TaskUpdate,TaskList,TaskGet";

    /// <summary>The PreToolUse hook command that refuses background shells.</summary>
    public string? TasksGuardCommand() => !TasksAvailable
        ? null
        : TasksHook("--pretooluse-hook");

    /// <summary>A hook command line for the tasks exe. Forward slashes and quotes: the CLI runs hooks through a shell that eats backslashes.</summary>
    private string TasksHook(string arguments) => $"\"{RunnablePath("Codale.Mcp.Tasks").Replace('\\', '/')}\" {arguments}";

    /// <summary>Standing instruction that sends long-running commands to the task tools.</summary>
    public const string TasksDirective =
        "Shell tools cannot run commands in the background here (run_in_background, &, Start-Job and nohup are refused). " +
        "For a dev server, watcher, watch-mode build or any command that keeps running, call mcp__codale-tasks__start_task; " +
        "it returns a task id, read_task follows the output and stop_task ends it. Codale shows the task and its live output " +
        "to the user. Quick commands that finish in seconds still run normally in the foreground shell. " +
        "The project keeps a saved command list that the user sees in Codale's commands menu: list_commands shows it, " +
        "add_command saves a reusable run command to it (saving only, nothing runs), and run_command runs a saved one by name as a task. " +
        "The user's own terminal tabs are readable too: when they say a command in their terminal failed or ask you to look at its " +
        "result, call list_terminals and then read_terminal to diagnose the output (read-only). " +
        "Codale's session panel is fed by two codale-tasks tools and nothing else. " +
        "Show your progress with mcp__codale-tasks__todos_set: pass the whole step list every call (2-6 short steps, one in_progress, " +
        "finished ones completed) when you start, as you begin each step and when the last one finishes. The built-in task-list tools " +
        "(TodoWrite, TaskCreate, TaskUpdate) are switched off; do not look for them. " +
        "Use mcp__codale-tasks__artifact_add for a deliverable that is not project source (a report, document, image or data file): " +
        "pass a path or markdown and it is listed for the user to open; do not add source files you edited. " +
        "If these tools are not directly available, load them with one ToolSearch (select:mcp__codale-tasks__todos_set,mcp__codale-tasks__artifact_add).";

    /// <summary>The servers to attach to a session started now; a server whose exe did not ship is left out.</summary>
    public IReadOnlyList<McpServerSpec> Servers()
    {
        var servers = new List<McpServerSpec>();

        if (BrowserEnabled && BrowserAvailable)
        {
            servers.Add(new McpServerSpec
            {
                Name = BrowserName,
                Command = RunnablePath("Codale.Mcp.Browser"),
                AutoApprove = true,

                // Uploads and script evaluation reach beyond the sandbox, so these still ask.
                AskTools = ["browser_upload", "browser_evaluate"],

                // browser_upload is confined to this folder and refuses without it.
                Env = new Dictionary<string, string> { ["CODALE_PROJECT_DIR"] = _projectPath },
            });
        }

        if (ComputerEnabled && ComputerAvailable)
        {
            servers.Add(new McpServerSpec
            {
                Name = ComputerName,
                Command = RunnablePath("Codale.Mcp.Computer"),
                RequireApproval = true,
            });
        }

        return servers;
    }

    /// <summary>
    /// Synchronizes MCP server configuration into the workspace's <c>.agents/mcp_config.json</c>
    /// so that custom CLIs such as Google Antigravity (<c>agy</c>) discover and attach
    /// Codale's tools (browser, computer, tasks) automatically.
    /// </summary>
    public void SyncWorkspaceMcpConfig(string? targetDirectory = null, IReadOnlyList<McpServerSpec>? servers = null)
    {
        try
        {
            servers ??= TerminalServers();
            var json = McpServerSpec.ToClaudeConfig(servers);
            var dir = targetDirectory ?? _projectPath;
            var agentsDir = Path.Combine(dir, ".agents");
            var configPath = Path.Combine(agentsDir, "mcp_config.json");

            if (json is null)
            {
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
            }
            else
            {
                Directory.CreateDirectory(agentsDir);
                File.WriteAllText(configPath, json);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Failed to sync workspace MCP config: {ex.Message}");
        }

        // CLIs with no launch flag for MCP servers read these project files instead (Claude gets
        // its servers as --mcp-config, see RewriteCliLaunch). They are shared with the user's own
        // entries, so only the codale-* servers are touched.
        var root = targetDirectory ?? _projectPath;
        servers ??= TerminalServers();
        MergeServers(Path.Combine(root, ".gemini", "settings.json"), servers, McpFileFormat.Gemini);
        MergeServers(Path.Combine(root, ".qwen", "settings.json"), servers, McpFileFormat.Gemini);
        MergeServers(Path.Combine(root, "opencode.json"), servers, McpFileFormat.OpenCode);

        // Earlier builds wrote Claude's servers here; remove ours so they are not registered twice.
        MergeServers(Path.Combine(root, ".mcp.json"), [], McpFileFormat.Standard);
    }

    private static readonly HashSet<string> ClaudeNonSessionArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcp", "config", "update", "upgrade", "doctor", "install", "migrate-installer", "setup-token", "plugin", "agents",
        "-v", "--version", "-h", "--help", "--mcp-config",
    };

    /// <summary>
    /// For a line typed at a shell prompt that launches Claude Code, the command to run instead:
    /// the same line with Codale's servers attached through <c>--mcp-config</c>, so no restart or
    /// project file is needed. Null for anything else (other commands, subcommands, pipes).
    /// </summary>
    public string? RewriteCliLaunch(string promptLine)
    {
        try
        {
            if (Codale.Agents.CliLocator.DetectLaunch(promptLine) is not ({ Id: "claude" }, var rest))
            {
                return null;
            }

            if (rest.IndexOfAny(['|', ';', '&', '<', '>']) >= 0 ||
                rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(ClaudeNonSessionArgs.Contains))
            {
                return null;
            }

            var servers = TerminalServers();
            if (servers.Count == 0 || McpServerSpec.ToClaudeConfig(servers) is not { } json)
            {
                return null;
            }

            var directory = Path.Combine(StagingRoot(), "cli");
            Directory.CreateDirectory(directory);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_projectPath)))[..12];
            var path = Path.Combine(directory, $"claude-{hash}.json");
            File.WriteAllText(path, json);

            // Last, because --mcp-config takes any number of values and would swallow a trailing prompt.
            return $"claude{rest.TrimEnd()} --mcp-config \"{path}\"";
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Failed to rewrite CLI launch: {ex.Message}");
            return null;
        }
    }

    private enum McpFileFormat
    {
        /// <summary><c>mcpServers</c> with a <c>type</c> field (Claude's <c>.mcp.json</c>).</summary>
        Standard,

        /// <summary><c>mcpServers</c> without <c>type</c>, plus <c>trust</c> (Gemini CLI and Qwen Code).</summary>
        Gemini,

        /// <summary>OpenCode's <c>mcp</c> map: <c>type: local</c>, a <c>command</c> array and an <c>environment</c> map.</summary>
        OpenCode,
    }

    /// <summary>
    /// Writes <paramref name="servers"/> into the MCP map of a CLI's JSON config,
    /// keeping every other key and every server not owned by Codale. Stale <c>codale-*</c>
    /// entries are dropped; a file Codale created and left empty is deleted.
    /// </summary>
    private static void MergeServers(string path, IReadOnlyList<McpServerSpec> servers, McpFileFormat format)
    {
        var mapKey = format == McpFileFormat.OpenCode ? "mcp" : "mcpServers";
        var gemini = format == McpFileFormat.Gemini;
        try
        {
            var existed = File.Exists(path);
            if (!existed && servers.Count == 0)
            {
                return;
            }

            System.Text.Json.Nodes.JsonObject doc;
            if (existed)
            {
                try
                {
                    doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions
                    {
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) as System.Text.Json.Nodes.JsonObject ?? new();
                }
                catch (System.Text.Json.JsonException)
                {
                    // The user's file is malformed; leave it alone rather than overwrite it.
                    return;
                }
            }
            else
            {
                doc = new();
            }

            var map = doc[mapKey] as System.Text.Json.Nodes.JsonObject ?? new();
            foreach (var key in map.Select(p => p.Key).Where(k => k.StartsWith("codale-", StringComparison.Ordinal)).ToList())
            {
                map.Remove(key);
            }

            if (servers.Count > 0 && System.Text.Json.Nodes.JsonNode.Parse(McpServerSpec.ToClaudeConfig(servers)!)?["mcpServers"] is System.Text.Json.Nodes.JsonObject ours)
            {
                foreach (var (name, entry) in ours.ToList())
                {
                    ours.Remove(name);
                    if (format == McpFileFormat.OpenCode)
                    {
                        var spec = servers.First(s => s.Name == name);
                        var local = new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "local",
                            ["command"] = new System.Text.Json.Nodes.JsonArray(
                                new[] { spec.Command }.Concat(spec.Args).Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                            ["enabled"] = true,
                        };
                        if (spec.Env is { Count: > 0 } env)
                        {
                            var vars = new System.Text.Json.Nodes.JsonObject();
                            foreach (var (key, value) in env)
                            {
                                vars[key] = value;
                            }

                            local["environment"] = vars;
                        }

                        map[name] = local;
                        continue;
                    }

                    if (gemini && entry is System.Text.Json.Nodes.JsonObject e)
                    {
                        // Gemini has no "type" field; "trust" skips its per-call confirmation.
                        e.Remove("type");
                        var spec = servers.First(s => s.Name == name);
                        if (spec.AutoApprove && !spec.RequireApproval && spec.AskTools.Count == 0)
                        {
                            e["trust"] = true;
                        }
                    }

                    map[name] = entry;
                }
            }

            if (map.Count == 0)
            {
                doc.Remove(mapKey);
            }
            else
            {
                doc[mapKey] = map;
            }

            if (doc.Count == 0)
            {
                if (existed)
                {
                    File.Delete(path);
                }

                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Failed to merge MCP config into {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// A standing instruction for the enabled servers, appended to the agent's system prompt; null when none.
    /// Pass the <paramref name="servers"/> already computed for the session to skip a second scan.
    /// </summary>
    public string? Directive(IReadOnlyList<McpServerSpec>? servers = null)
    {
        servers ??= Servers();
        var parts = new List<string>();

        if (servers.Any(s => s.Name == BrowserName))
        {
            parts.Add(
                $"You can drive a real Chromium browser with the {McpServerSpec.PrefixOf(BrowserName)}* tools (Codale's own automation " +
                "browser, not the user's). After you change something a browser can show (a web page, UI, or a local dev server), open it with " +
                "browser_navigate, read it with browser_snapshot, look at it with browser_screenshot, exercise the changed flow, and check " +
                "browser_console_messages for errors, before you report the work as done. The browser is hidden by default and keeps logins " +
                "between runs. When a page needs a sign-in, captcha or 2FA, do not give up or ask for credentials: call browser_handoff and the " +
                "user completes it in a visible window. To check responsive layouts use browser_set_viewport (mobile, tablet, laptop, desktop, 4k) " +
                "or browser_responsive_check for screenshots at several sizes in one call; browser_emulate switches dark mode. " +
                "Element refs (e12) come from the latest browser_snapshot and expire at the next one.");
        }

        if (servers.Any(s => s.Name == ComputerName))
        {
            parts.Add(
                $"You can also see and control the user's Windows desktop with the {McpServerSpec.PrefixOf(ComputerName)}* tools, for what " +
                "the browser tools cannot reach: native apps, installers, system dialogs, the running build of a desktop app. They act on the " +
                "real screen, so take a computer_screenshot before every click (coordinates refer to the latest screenshot) and the user approves each action.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private string ExePath(string name) => Path.Combine(_baseDirectory, name, name + ".exe");

    /// <summary>
    /// The exe as a spawned agent can launch it. A package's install folder
    /// (WindowsApps) cannot be executed by processes outside the package, and the agent
    /// CLI spawns MCP servers itself (<c>spawn EPERM</c>), so a packaged server is first
    /// copied to a plain folder, once per build, and run from there.
    /// </summary>
    private string RunnablePath(string name)
    {
        var source = ExePath(name);
        if (!source.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        // Every Servers() and hook call asks; the stamp scan and staging check run once per name.
        lock (_runnable)
        {
            if (_runnable.TryGetValue(name, out var known) && File.Exists(known))
            {
                return known;
            }

            // A failed staging falls back to the source and is retried next time.
            var staged = StageRunnable(name, source);
            if (!string.Equals(staged, source, StringComparison.OrdinalIgnoreCase))
            {
                _runnable[name] = staged;
            }

            return staged;
        }
    }

    private readonly Dictionary<string, string> _runnable = new(StringComparer.Ordinal);

    private static string StageRunnable(string name, string source)
    {
        try
        {
            // The exe is a small apphost that rarely changes between builds; the code
            // lives in the dlls beside it (and in subfolders such as the browser's driver),
            // so the stamp covers every file under the folder.
            var files = new DirectoryInfo(Path.GetDirectoryName(source)!).GetFiles("*", SearchOption.AllDirectories);
            var stamp = $"{files.Length:x}-{files.Sum(f => f.Length):x}-{files.Max(f => f.LastWriteTimeUtc.Ticks):x}";
            var target = Path.Combine(StagingRoot(), name, stamp);
            var staged = Path.Combine(target, name + ".exe");
            if (File.Exists(staged))
            {
                return staged;
            }

            var partial = target + ".partial";
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            CopyDirectory(Path.GetDirectoryName(source)!, partial);
            Directory.Move(partial, target);

            // Older builds' copies are dead weight (the browser one carries a Node runtime).
            foreach (var old in Directory.GetDirectories(Path.Combine(StagingRoot(), name)))
            {
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(old, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use by a running session */ }
                }
            }

            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Trace($"MCP staging failed for {name}: {ex.Message}");
            return source;
        }
    }

    /// <summary>
    /// A physical folder other processes see at the same path. A packaged app's
    /// %LOCALAPPDATA% is virtualised, so it is not that; the package's local cache is.
    /// </summary>
    private static string StagingRoot()
    {
        string root;
        try
        {
            root = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch (Exception)
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(root, "Codale", "mcp");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
