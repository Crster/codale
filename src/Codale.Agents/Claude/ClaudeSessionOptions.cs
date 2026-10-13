namespace Codale.Agents.Claude;

public sealed record ClaudeSessionOptions
{
    /// <summary>Working directory for the agent: the project root, or a session worktree.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The CLI to launch: the located install (npm, ~/.local/bin, PATHEXT) or, failing that, the bare command name.</summary>
    public string Executable { get; init; } = CliLocator.Find(CliLocator.Executable) ?? CliLocator.Executable;

    /// <summary>Model alias ("opus", "sonnet", "haiku") or a full model id. Null uses the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>How hard the model thinks: low, medium, high, xhigh or max. Null uses the CLI default.</summary>
    public string? Effort { get; init; }

    /// <summary>The choices the CLI's --effort flag accepts (claude 2.7).</summary>
    private static readonly HashSet<string> AllowedEfforts = new(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh", "max",
    };

    /// <summary>
    /// The CLI exits rather than tolerate an unknown effort, and effort vocabularies
    /// drift per provider - so anything this CLI does not name falls back to its default.
    /// </summary>
    public static string? ClampEffort(string? effort) =>
        effort is { Length: > 0 } && AllowedEfforts.Contains(effort) ? effort : null;

    /// <summary>
    /// Start mode. "auto" (the default) runs without asking; "manual" routes every
    /// tool through the host approval dialog; "acceptEdits" auto-approves edits.
    /// The user can change this mid-session. Null/empty passes no flag: the CLI starts in its own default.
    /// </summary>
    public string? PermissionMode { get; init; } = DefaultPermissionMode;

    /// <summary>The mode a fresh session starts in: Automatic.</summary>
    public const string DefaultPermissionMode = "auto";

    /// <summary>The choices the CLI's --permission-mode flag actually accepts (claude 2.x).</summary>
    private static readonly HashSet<string> AllowedPermissionModes = new(StringComparer.Ordinal)
    {
        "manual", "acceptEdits", "auto", "bypassPermissions", "dontAsk", "plan",
    };

    /// <summary>
    /// Permission modes are per-provider dialects: a stored value may predate a mode's
    /// rename, and the stream parser falls back to "default", but the CLI exits
    /// on start rather than tolerate an unknown choice. Anything foreign becomes the default.
    /// </summary>
    public static string ClampPermissionMode(string mode) =>
        AllowedPermissionModes.Contains(mode) ? mode : DefaultPermissionMode;

    /// <summary>Fixed id so the transcript file is predictable. Generated when null.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Resume an existing conversation instead of starting a new one.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Appended to the CLI's own system prompt at spawn, for host-level standing
    /// instructions - e.g. "always plan the work as task-tool steps first". Unlike a
    /// per-turn prefix it never appears in the conversation the user reads back.
    /// </summary>
    public string? SystemPromptAppend { get; init; }

    /// <summary>Stream partial text as it is generated. Off makes the UI wait for whole messages.</summary>
    public bool IncludePartialMessages { get; init; } = true;

    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Host-provided MCP servers (browser, desktop control), passed with --mcp-config on
    /// top of whatever the user has configured. Servers that require approval also get
    /// an ask rule via --settings, which outranks the permission mode.
    /// </summary>
    public IReadOnlyList<Codale.Core.Agents.McpServerSpec> McpServers { get; init; } = [];

    /// <summary>
    /// Command line of a <c>PreToolUse</c> hook that refuses shell calls asking to run in
    /// the background (registered through <c>--settings</c>). Null registers no hook.
    /// </summary>
    public string? BackgroundShellGuardCommand { get; init; }

    /// <summary>
    /// The host's token-saving hooks and deny rules (output condensing, the large-file read
    /// guard). Its shell guard, when unset, falls back to <see cref="BackgroundShellGuardCommand"/>.
    /// </summary>
    public Codale.Core.Agents.ClaudeHookSettings? Hooks { get; init; }

    /// <summary>Comma-separated tool names removed from the model's context (--disallowedTools); null removes none.</summary>
    public string? DisallowedTools { get; init; }

    /// <summary>
    /// Subagent definitions for this session (--agents JSON); one named like a built-in
    /// (Explore, general-purpose) replaces it. Null keeps the CLI's own.
    /// </summary>
    public string? Agents { get; init; }

    /// <summary>
    /// Extra environment variables for the CLI process, layered over the inherited
    /// environment - how a custom API endpoint (ANTHROPIC_BASE_URL and friends)
    /// reaches the CLI. Null leaves the environment untouched.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Ask the CLI to predict the next user prompt after each turn (the initialize request's promptSuggestions).</summary>
    public bool PromptSuggestions { get; init; }

    /// <summary>
    /// When set, every raw stdout line is appended to this file. The protocol is
    /// undocumented and version-drifts, so being able to capture a verbatim
    /// transcript is how we diagnose and turn a surprise into a replay fixture.
    /// </summary>
    public string? RawLogPath { get; init; }

    internal IReadOnlyList<string> BuildArguments()
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",

            // The host answers tool permission prompts over the stdio control channel.
            // Without this the CLI silently denies and emits system/permission_denied.
            "--permission-prompt-tool", "stdio",


            // Echo our own user messages back so the transcript has a single ordering authority.
            "--replay-user-messages",
        };

        if (PermissionMode is { Length: > 0 } permissionMode)
        {
            args.AddRange(["--permission-mode", permissionMode]);
        }

        // Full is bypassPermissions; without this the CLI refuses a live switch into it
        // (and may start in "default"), so Full would silently fall back to Manual.
        args.Add("--allow-dangerously-skip-permissions");

        if (IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }

        if (Model is { Length: > 0 } model)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (SystemPromptAppend is { Length: > 0 } append)
        {
            args.Add("--append-system-prompt");
            args.Add(append);
        }

        if (Effort is { Length: > 0 } effort)
        {
            args.Add("--effort");
            args.Add(effort);
        }

        if (ResumeSessionId is { Length: > 0 } resume)
        {
            args.Add("--resume");
            args.Add(resume);
        }
        else if (SessionId is { } id)
        {
            args.Add("--session-id");
            args.Add(id.ToString());
        }

        foreach (var dir in AdditionalDirectories)
        {
            args.Add("--add-dir");
            args.Add(dir);
        }

        if (Codale.Core.Agents.McpServerSpec.ToClaudeConfig(McpServers) is { } mcpConfig)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfig);
        }

        var hooks = Hooks ?? new Codale.Core.Agents.ClaudeHookSettings();
        if (hooks.ShellGuardCommand is null)
        {
            hooks = hooks with { ShellGuardCommand = BackgroundShellGuardCommand };
        }

        if (Codale.Core.Agents.McpServerSpec.ToClaudeSettings(McpServers, hooks) is { } settings)
        {
            args.Add("--settings");
            args.Add(settings);
        }

        if (DisallowedTools is { Length: > 0 } disallowed)
        {
            args.Add("--disallowedTools");
            args.Add(disallowed);
        }

        if (Agents is { Length: > 0 } agents)
        {
            args.Add("--agents");
            args.Add(agents);
        }

        args.AddRange(ExtraArguments);
        return args;
    }
}
