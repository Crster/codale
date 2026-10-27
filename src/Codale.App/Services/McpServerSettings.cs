using Codale.Core.Agents;
using Codale.Mcp;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// Which of Codale's built-in MCP servers a project's agents get: a browser the agent
/// can drive to test and verify what it built, and desktop control (screenshots, mouse,
/// keyboard) for everything a browser cannot reach, and an Android phone (adb) to build and
/// test mobile apps on. The browser is a global preference that defaults on; desktop and
/// phone control act on real hardware, so they are per project and default off. Like <see cref="CliEndpointSettings"/> the values are re-read on every
/// access, so a toggle applies to the next session start.
/// </summary>
public sealed class McpServerSettings
{
    public const string BrowserName = "codale-browser";
    public const string ComputerName = "codale-computer";
    public const string PhoneName = "codale-phone";

    private const string BrowserKey = "mcp.browser.enabled";
    private const string ComputerKey = "mcp.computer.enabled";
    private const string PhoneKey = "mcp.phone.enabled";

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
    /// The servers to write into the global Codale config (%LOCALAPPDATA%\Codale) when no launch is
    /// at hand, supplied by the workspace so they include the tasks server of the terminal session on the panel.
    /// A launch itself passes its own session's servers (see <see cref="RewriteCliLaunch"/>). Unset, the servers started now are used.
    /// </summary>
    public Func<IReadOnlyList<McpServerSpec>>? TerminalServersProvider { get; set; }

    private IReadOnlyList<McpServerSpec> TerminalServers() => ForTerminal(TerminalServersProvider?.Invoke() ?? Servers());

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

    /// <summary>Off unless the user turned it on for this project: it acts on a real attached device.</summary>
    public bool PhoneEnabled
    {
        get => _store.GetUiState(_projectPath, PhoneKey) == "1";
        set => _store.SetUiState(_projectPath, PhoneKey, value ? "1" : "0");
    }

    public bool PhoneAvailable => File.Exists(ExePath("Codale.Mcp.Phone"));

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
    /// <param name="pushToPanel">For an agent CLI in a terminal, whose tool stream the app cannot read: todos_set and artifact_add are sent over the pipe.</param>
    public McpServerSpec? TasksSpec(string pipeName, string token, string? runDirectory = null, bool pushToPanel = false) => !TasksAvailable
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
                ["CODALE_PANEL_PUSH"] = pushToPanel ? "1" : "0",
            },
        };

    /// <summary>The CLI's own task-list tools, switched off when todos_set is available so the panel has one source.</summary>
    public const string NativeTodoTools = "TodoWrite,TaskCreate,TaskUpdate,TaskList,TaskGet";

    /// <summary>The PreToolUse hook command that refuses background shells.</summary>
    public string? TasksGuardCommand() => !TasksAvailable
        ? null
        : TasksHook("--pretooluse-hook");

    /// <summary>A hook command line for the tasks exe. Forward slashes and quotes: the CLI runs hooks through a shell that eats backslashes.</summary>
    private string TasksHook(string arguments) => $"\"{RunnablePath("Codale.Mcp.Tasks").Replace('\\', '/')}\" {arguments}";

    /// <summary>Standing instruction that sends long-running commands to the task tools.</summary>
    /// <remarks>
    /// It rides in the system prompt of every request a session makes, so every word is paid
    /// for on each call (at the cache-read price at best): facts only, no rationale.
    /// </remarks>
    public const string TasksDirective =
        "Background shells are refused here (run_in_background, &, Start-Job, nohup). For a dev server, watcher or any command " +
        "that keeps running, call mcp__codale-tasks__start_task (read_task follows its output, stop_task ends it); quick commands run normally. " +
        "Saved project commands: list_commands, add_command (saves only), run_command (runs one by name as a task). " +
        "When the user's own terminal output matters, list_terminals then read_terminal (read-only). " +
        "To view static files in a browser, serve_folder hosts them on http://localhost:PORT/ until stop_serving; do not start_task an http server for that. " +
        "MANDATORY when a request needs tools: your FIRST action is mcp__codale-tasks__todos_set with the whole list (2-6 short steps, one in_progress), " +
        "called again as each step starts and when the last finishes. TodoWrite, TaskCreate and TaskUpdate are off; do not look for them. " +
        "Deliverables that are not project source (report, document, image, data) go to mcp__codale-tasks__artifact_add (a path or markdown). " +
        "If these tools are not loaded, load them with one ToolSearch (select:mcp__codale-tasks__todos_set,mcp__codale-tasks__artifact_add).";

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
                AutoApprove = true,

                // Looking, moving the pointer and clicking run unasked; typing and key presses can enter commands or data, so those ask.
                AskTools = ["computer_type", "computer_key"],
            });
        }

        if (PhoneEnabled && PhoneAvailable)
        {
            servers.Add(new McpServerSpec
            {
                Name = PhoneName,
                Command = RunnablePath("Codale.Mcp.Phone"),
                AutoApprove = true,

                // Only what changes or erases device state, writes to it, or runs arbitrary commands asks.
                AskTools = ["phone_install", "phone_uninstall", "phone_clear_data", "phone_push", "phone_shell"],

                // phone_install, phone_push and phone_pull are confined to this folder and refuse without it.
                Env = new Dictionary<string, string> { ["CODALE_PROJECT_DIR"] = _projectPath },
            });
        }

        return servers;
    }

    /// <summary>
    /// Injects Codale's global MCP configuration and rules into the app's local data folder
    /// (<c>%LOCALAPPDATA%\Codale</c>) so they are available across all workspaces without dirtying
    /// any project repository.
    /// </summary>
    public void SyncWorkspaceMcpConfig(string? targetDirectory = null, IReadOnlyList<McpServerSpec>? servers = null)
    {
        servers = ForTerminal(servers ?? TerminalServers());

        try
        {
            WriteGlobalMcpConfig(servers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Trace($"Failed to sync MCP config: {ex.Message}");
        }
    }

    /// <summary>
    /// Injects the global MCP configuration into the local app folder (<c>%LOCALAPPDATA%\Codale</c>).
    /// </summary>
    public static void WriteGlobalMcpConfig(IReadOnlyList<McpServerSpec> servers)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CodaleDataRoot(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale"),
        };

        foreach (var dir in targets)
        {
            try
            {
                var json = McpServerSpec.ToAgyConfig(servers);
                var rootConfig = Path.Combine(dir, "mcp_config.json");
                var agentsConfig = Path.Combine(dir, ".agents", "mcp_config.json");

                if (json is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(rootConfig, json);

                    Directory.CreateDirectory(Path.GetDirectoryName(agentsConfig)!);
                    File.WriteAllText(agentsConfig, json);
                }
                else
                {
                    if (File.Exists(rootConfig)) File.Delete(rootConfig);
                    if (File.Exists(agentsConfig)) File.Delete(agentsConfig);
                }

                WriteAgyRules(Path.Combine(dir, ".agents", "rules", "codale.md"), servers);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLog.Trace($"Failed to write global MCP config to {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The servers as handed to a CLI in a terminal: each sends its usage instructions at connect
    /// (<see cref="McpInstructions"/>), which the panel's own session gets through its system prompt instead.
    /// </summary>
    private static IReadOnlyList<McpServerSpec> ForTerminal(IReadOnlyList<McpServerSpec> servers) =>
        [.. servers.Select(s => s with
        {
            Env = new Dictionary<string, string>(s.Env ?? new Dictionary<string, string>())
            {
                [McpInstructions.EnvironmentVariable] = "1",
            },
        })];

    /// <summary>
    /// The same instructions as a workspace rule, for Antigravity: whether it reads a server's
    /// instructions is not documented, and its rules folder is read at startup.
    /// </summary>
    private static void WriteAgyRules(string path, IReadOnlyList<McpServerSpec> servers)
    {
        var parts = new List<string>();
        if (servers.Any(s => s.Name == TasksName))
        {
            parts.Add($"codale-tasks: {McpInstructions.Tasks}");
        }

        if (servers.Any(s => s.Name == BrowserName))
        {
            parts.Add($"{BrowserName}: {McpInstructions.Browser}");
        }

        if (servers.Any(s => s.Name == ComputerName))
        {
            parts.Add($"{ComputerName}: {McpInstructions.Computer}");
        }

        if (servers.Any(s => s.Name == PhoneName))
        {
            parts.Add($"{PhoneName}: {McpInstructions.Phone}");
        }

        if (parts.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\ntrigger: always_on\n---\n\n# Codale\n\n" + string.Join("\n\n", parts) + "\n");
    }



    /// <summary>Arguments that run a CLI's housekeeping (setup, updates, its own MCP management) rather than a session.</summary>
    private static readonly HashSet<string> NonSessionArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcp", "config", "update", "upgrade", "doctor", "install", "migrate-installer", "setup-token", "plugin", "plugins",
        "agents", "models", "changelog", "help", "login", "logout", "auth", "extensions",
        "-v", "--version", "-h", "--help", "--mcp-config", "--additional-mcp-config",
    };

    /// <summary>True when the prompt line starts an agent CLI session: a known CLI, plain arguments, no housekeeping subcommand.</summary>
    public static bool IsSessionLaunch(string promptLine)
    {
        if (Codale.Agents.CliLocator.DetectLaunch(promptLine) is not (_, var rest))
        {
            return false;
        }

        return rest.IndexOfAny(['|', ';', '&', '<', '>', '`', '$']) < 0 &&
            !rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(NonSessionArgs.Contains);
    }

    /// <summary>
    /// For a line typed at a shell prompt that starts an agent CLI session, the command to run instead:
    /// the same launch with Codale's servers attached the way that CLI takes them at startup, so nothing
    /// is written into the project. Null to run the line as typed (other commands, housekeeping
    /// subcommands and pipes).
    /// </summary>
    /// <remarks>
    /// Claude: <c>--mcp-config</c>. Copilot: <c>--additional-mcp-config</c>. Codex: <c>-c mcp_servers.*</c> overrides.
    /// Gemini CLI and Qwen Code: a system settings file named by <c>GEMINI_CLI_SYSTEM_SETTINGS_PATH</c> /
    /// <c>QWEN_CODE_SYSTEM_SETTINGS_PATH</c>, built on the machine's own system settings so none are lost.
    /// OpenCode: a config file named by <c>OPENCODE_CONFIG</c>, merged with the user's and project's.
    /// Antigravity: <c>--add-dir</c> pointing to Codale's global app local folder.
    /// </remarks>
    public string? RewriteCliLaunch(string promptLine, IReadOnlyList<McpServerSpec> sessionServers, string key)
    {
        try
        {
            if (!IsSessionLaunch(promptLine) ||
                Codale.Agents.CliLocator.DetectLaunch(promptLine) is not ({ } cli, var rest))
            {
                return null;
            }

            var servers = ForTerminal(sessionServers);
            if (servers.Count == 0)
            {
                return null;
            }

            var powershell = promptLine.StartsWith("PS ", StringComparison.Ordinal);
            var tail = rest.TrimEnd();
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            return cli.Id switch
            {
                // Last, because --mcp-config takes any number of values and would swallow a trailing prompt.
                "claude" => StageClaude(key, servers, tail, powershell),
                "copilot" => $"copilot --additional-mcp-config {Quote(powershell, "@" + Stage("copilot", key, CopilotConfig(servers)))}{tail}",
                "codex" => $"codex {CodexOverrides(powershell, servers)}{tail}",
                "gemini" => WithEnv(powershell, "GEMINI_CLI_SYSTEM_SETTINGS_PATH",
                    StageSettings("gemini", key, servers, Path.Combine(programData, "gemini-cli", "settings.json"), "GEMINI_CLI_SYSTEM_SETTINGS_PATH"),
                    "gemini" + tail),
                "qwen" => WithEnv(powershell, "QWEN_CODE_SYSTEM_SETTINGS_PATH",
                    StageSettings("qwen", key, servers, Path.Combine(programData, "qwen-code", "settings.json"), "QWEN_CODE_SYSTEM_SETTINGS_PATH"),
                    "qwen" + tail),
                "opencode" => WithEnv(powershell, "OPENCODE_CONFIG", StageOpenCode(key, servers), "opencode" + tail),
                "agy" => StageAgy(key, servers, tail, powershell),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Failed to rewrite CLI launch: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a launch config for one CLI session into Codale's staging folder, outside the project.</summary>
    private string Stage(string cli, string key, string content)
    {
        var path = StagedPath(cli, key);
        File.WriteAllText(path, content);
        return path;
    }

    private string StageClaude(string key, IReadOnlyList<McpServerSpec> servers, string tail, bool powershell)
    {
        var configPath = Stage("claude", key, McpServerSpec.ToClaudeConfig(servers)!);
        var settingsPath = StagedPath("claude-settings", key);
        File.WriteAllText(settingsPath, McpServerSpec.ToClaudeSettings(servers, new ClaudeHookSettings()) ?? "{}");

        var sb = new System.Text.StringBuilder();
        sb.Append("claude");
        sb.Append(tail);
        sb.Append(" --mcp-config ");
        sb.Append(Quote(powershell, configPath));
        sb.Append(" --settings ");
        sb.Append(Quote(powershell, settingsPath));
        return sb.ToString();
    }

    private string StageAgy(string key, IReadOnlyList<McpServerSpec> servers, string tail, bool powershell)
    {
        var stageDir = Path.Combine(StagingRoot(), "cli", $"agy-{ProjectHash()}-{key}");
        var agentsDir = Path.Combine(stageDir, ".agents");
        Directory.CreateDirectory(agentsDir);

        var json = McpServerSpec.ToAgyConfig(servers);
        if (json is { Length: > 0 })
        {
            File.WriteAllText(Path.Combine(stageDir, "mcp_config.json"), json);
            File.WriteAllText(Path.Combine(agentsDir, "mcp_config.json"), json);
        }

        WriteAgyRules(Path.Combine(agentsDir, "rules", "codale.md"), servers);

        var skip = tail.Contains("--dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase) ? "" : " --dangerously-skip-permissions";
        return $"agy --add-dir {Quote(powershell, stageDir)}{skip}{tail}";
    }

    /// <summary>One file per project and session: each CLI carries its own pipe and token, so two launches must not share one.</summary>
    private string StagedPath(string cli, string key)
    {
        var directory = Path.Combine(StagingRoot(), "cli");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{cli}-{ProjectHash()}-{key}.json");
    }

    private string ProjectHash() =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_projectPath)))[..12];

    /// <summary>Removes what a session's launch staged once the session has ended.</summary>
    public void ClearStaged(string key)
    {
        try
        {
            var directory = Path.Combine(StagingRoot(), "cli");
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, $"*-{ProjectHash()}-{key}*"))
                {
                    File.Delete(file);
                }

                foreach (var dir in Directory.EnumerateDirectories(directory, $"*-{ProjectHash()}-{key}*"))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Trace($"Failed to clear staged CLI config: {ex.Message}");
        }
    }

    /// <summary>
    /// A system settings file for Gemini CLI or Qwen Code: the machine's own (where the variable already
    /// points, else the default location) with Codale's servers merged in, since this one replaces it.
    /// </summary>
    private string StageSettings(string cli, string key, IReadOnlyList<McpServerSpec> servers, string defaultSystemPath, string variable)
    {
        var path = StagedPath(cli, key);
        var system = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : defaultSystemPath;
        if (File.Exists(system) && !string.Equals(Path.GetFullPath(system), path, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(system, path, overwrite: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        MergeServers(path, servers, McpFileFormat.Gemini);
        return path;
    }

    private string StageOpenCode(string key, IReadOnlyList<McpServerSpec> servers)
    {
        var path = StagedPath("opencode", key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        MergeServers(path, servers, McpFileFormat.OpenCode);
        return path;
    }

    /// <summary>Copilot CLI's MCP document: <c>mcpServers</c> entries of <c>type: local</c> with every tool allowed.</summary>
    private static string CopilotConfig(IReadOnlyList<McpServerSpec> servers)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(McpServerSpec.ToClaudeConfig(servers)!)!.AsObject();
        foreach (var (_, entry) in doc["mcpServers"]!.AsObject())
        {
            entry!["type"] = "local";
            entry["tools"] = new System.Text.Json.Nodes.JsonArray("*");
        }

        return doc.ToJsonString();
    }

    /// <summary>
    /// Codex's per-launch config overrides, one <c>-c key=value</c> per field. Values are TOML literal
    /// strings ('...'), so Windows paths need no escaping.
    /// </summary>
    private static string CodexOverrides(bool powershell, IReadOnlyList<McpServerSpec> servers)
    {
        static string Literal(string value) => $"'{value}'";

        var parts = new List<string>();
        foreach (var server in servers)
        {
            var key = $"mcp_servers.{server.Name}";
            parts.Add($"-c {Quote(powershell, $"{key}.command={Literal(server.Command)}")}");
            parts.Add($"-c {Quote(powershell, $"{key}.args=[{string.Join(",", server.Args.Select(Literal))}]")}");
            if (server.Env is { Count: > 0 } env)
            {
                parts.Add($"-c {Quote(powershell, $"{key}.env={{{string.Join(",", env.Select(e => $"{e.Key}={Literal(e.Value)}"))}}}")}");
            }

            if (server.RequireApproval)
            {
                parts.Add($"-c {Quote(powershell, $"{key}.default_tools_approval_mode='prompt'")}");
            }
            else if (server.AutoApprove)
            {
                parts.Add($"-c {Quote(powershell, $"{key}.default_tools_approval_mode='approve'")}");
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>One argument, quoted for the shell the line was typed into.</summary>
    private static string Quote(bool powershell, string value) => powershell
        ? $"'{value.Replace("'", "''")}'"
        : $"\"{value}\"";

    /// <summary>
    /// Runs <paramref name="command"/> with one environment variable set for it alone. PowerShell sets
    /// variables for the whole shell, so it is taken back when the CLI exits (finally runs on Ctrl+C too).
    /// </summary>
    private static string WithEnv(bool powershell, string name, string value, string command) => powershell
        ? $"& {{ $env:{name}={Quote(true, value)}; try {{ {command} }} finally {{ Remove-Item Env:{name} -ErrorAction SilentlyContinue }} }}"
        : $"set \"{name}={value}\" && {command} & set \"{name}=\"";

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
                        if (spec.AutoApprove && !spec.RequireApproval)
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
                $"The {McpServerSpec.PrefixOf(BrowserName)}* tools drive Codale's own Chromium (not the user's; hidden, keeps logins). " +
                "After changing something a browser can show, verify it before reporting done: browser_navigate, browser_snapshot (prefer it " +
                "over screenshots; its refs like e12 expire at the next snapshot), exercise the flow, check browser_console_messages. " +
                "For a sign-in, captcha or 2FA call browser_handoff; never ask for credentials. Layouts: browser_set_viewport or " +
                "browser_responsive_check; dark mode: browser_emulate.");
        }

        if (servers.Any(s => s.Name == ComputerName))
        {
            parts.Add(
                $"You can also see and control the user's Windows desktop with the {McpServerSpec.PrefixOf(ComputerName)}* tools, for what " +
                "the browser tools cannot reach: native apps, installers, system dialogs, the running build of a desktop app. They act on the " +
                "real screen, so take a computer_screenshot before every click (coordinates refer to the latest screenshot) and the user approves each action.");
        }

        if (servers.Any(s => s.Name == PhoneName))
        {
            parts.Add(
                $"You can control an Android phone or emulator over adb with the {McpServerSpec.PrefixOf(PhoneName)}* tools, to build and test " +
                "mobile apps. MANDATORY: NEVER execute raw 'adb' commands via terminal or shell tools. Always use the codale-phone tools instead. " +
                "Call phone_devices first, and take a phone_screenshot or phone_ui_tree before every tap or swipe (coordinates are " +
                "device pixels on the latest view). Build with the shell (e.g. gradlew installDebug; use start_task for long-running builds or " +
                "dev servers), install with phone_install, start the app with phone_launch_app and read phone_logcat for crashes before you " +
                "report the work as done. phone_port_forward (reverse) lets the phone reach a dev server on this computer.");
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
    /// The global Codale data directory in the user's local application data folder:
    /// <c>%LOCALAPPDATA%\Codale</c>.
    /// </summary>
    public static string CodaleDataRoot()
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

        return Path.Combine(root, "Codale");
    }

    /// <summary>
    /// A physical folder other processes see at the same path. A packaged app's
    /// %LOCALAPPDATA% is virtualised, so it is not that; the package's local cache is.
    /// </summary>
    private static string StagingRoot() => Path.Combine(CodaleDataRoot(), "mcp");

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