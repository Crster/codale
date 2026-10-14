namespace Codale.Agents.Codex;

public sealed record CodexSessionOptions
{
    /// <summary>Working directory for the agent: the project root, or a session worktree.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The CLI to launch: the located install (npm, ~/.local/bin, PATHEXT) or, failing that, the bare command name.</summary>
    public string Executable { get; init; } = CliLocator.Find("codex") ?? "codex";

    /// <summary>A model id the provider serves, passed per turn. Null uses the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>How hard the model thinks: low, medium, high or xhigh. Null uses the CLI default.</summary>
    public string? Effort { get; init; }

    /// <summary>The efforts turn/start accepts; codex has no "max".</summary>
    private static readonly HashSet<string> AllowedEfforts = new(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh",
    };

    /// <summary>Anything foreign falls back to the model's own default, matching the Claude driver's clamping.</summary>
    public static string? ClampEffort(string? effort) =>
        effort is { Length: > 0 } && AllowedEfforts.Contains(effort) ? effort : null;

    /// <summary>Start mode, in Codale's cross-CLI vocabulary; mapped to codex approval/sandbox per turn. Null starts in the CLI default.</summary>
    public string? PermissionMode { get; init; } = Claude.ClaudeSessionOptions.DefaultPermissionMode;

    /// <summary>The modes the rest of the app may hand us; anything else becomes the default.</summary>
    public static string ClampPermissionMode(string mode) => Claude.ClaudeSessionOptions.ClampPermissionMode(mode);

    /// <summary>
    /// Codale's permission mode in codex's own vocabulary: an approval policy plus a
    /// sandbox policy, applied per turn. Manual asks before anything untrusted; plan
    /// reads only; acceptEdits writes files itself but asks before commands; auto and
    /// dontAsk run without asking inside the workspace; bypass removes the sandbox too.
    /// </summary>
    public static (string ApprovalPolicy, string SandboxType) PermissionPolicy(string? mode) => ClampPermissionMode(mode ?? "") switch
    {
        "manual" => ("untrusted", "readOnly"),
        "plan" => ("on-request", "readOnly"),
        "acceptEdits" => ("on-request", "workspaceWrite"),
        "bypassPermissions" => ("never", "dangerFullAccess"),
        "dontAsk" => ("never", "workspaceWrite"),
        _ => ("never", "workspaceWrite"), // auto
    };

    /// <summary>Standing instructions for the thread (developer instructions), such as how to use the host's MCP tools.</summary>
    public string? SystemPromptAppend { get; init; }

    /// <summary>Resume an existing conversation (thread) instead of starting a new one.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Host-provided MCP servers, passed as <c>-c mcp_servers.*</c> config overrides -
    /// the same rendering the terminal-CLI launcher uses.
    /// </summary>
    public IReadOnlyList<Codale.Core.Agents.McpServerSpec> McpServers { get; init; } = [];

    /// <summary>
    /// Extra environment variables for the CLI process, layered over the inherited
    /// environment - how a custom API endpoint (OPENAI_BASE_URL and friends) reaches the CLI.
    /// Null leaves the environment untouched.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Custom model provider settings for Codex CLI: overrides model_provider, base_url,
    /// env_key, and wire_api via -c flags so Codex connects to the custom endpoint.
    /// </summary>
    public CodexCustomProvider? CustomProvider { get; init; }

    /// <summary>
    /// When set, every raw stdout line is appended to this file, so a protocol surprise
    /// becomes a diagnosable (and replayable) transcript.
    /// </summary>
    public string? RawLogPath { get; init; }

    /// <summary>Builds the <c>codex app-server</c> argument list. Exposed for the tests that pin it.</summary>
    internal IReadOnlyList<string> BuildArguments()
    {
        // Config overrides come before the subcommand, as the CLI's own docs show.
        var args = new List<string>();

        if (CustomProvider is { } provider)
        {
            static string Literal(string value) => $"'{value}'";
            const string id = "custom";
            args.Add("-c");
            args.Add($"model_provider={Literal(id)}");
            args.Add("-c");
            args.Add($"model_providers.{id}.name={Literal(provider.Name)}");
            args.Add("-c");
            args.Add($"model_providers.{id}.base_url={Literal(provider.BaseUrl)}");
            args.Add("-c");
            args.Add($"model_providers.{id}.env_key={Literal(provider.EnvKey)}");
            args.Add("-c");
            args.Add($"model_providers.{id}.wire_api={Literal(provider.WireApi)}");
            args.Add("-c");
            args.Add($"model_providers.{id}.supports_websockets={(provider.SupportsWebsockets ? "true" : "false")}");
        }

        if (Model is { Length: > 0 } model)
        {
            args.Add("-c");
            args.Add($"model='{model}'");
        }

        foreach (var (key, value) in McpOverrides(McpServers))
        {
            args.Add("-c");
            args.Add($"{key}={value}");
        }

        args.Add("app-server");
        return args;
    }

    /// <summary>
    /// MCP servers as TOML config override pairs: <c>mcp_servers.NAME.command</c>,
    /// <c>.args</c> and <c>.env</c>, values as TOML literal strings. Exposed for the tests.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> McpOverrides(IReadOnlyList<Codale.Core.Agents.McpServerSpec> servers)
    {
        static string Literal(string value) => $"'{value}'";

        var pairs = new List<(string, string)>();
        foreach (var server in servers)
        {
            var key = $"mcp_servers.{server.Name}";
            pairs.Add(($"{key}.command", Literal(server.Command)));
            pairs.Add(($"{key}.args", $"[{string.Join(",", server.Args.Select(Literal))}]"));
            if (server.Env is { Count: > 0 } env)
            {
                pairs.Add(($"{key}.env", $"{{{string.Join(",", env.Select(e => $"{e.Key}={Literal(e.Value)}"))}}}"));
            }
        }

        return pairs;
    }
}

public sealed record CodexCustomProvider(
    string Name,
    string BaseUrl,
    string EnvKey = "OPENAI_API_KEY",
    string WireApi = "responses",
    bool SupportsWebsockets = false);
