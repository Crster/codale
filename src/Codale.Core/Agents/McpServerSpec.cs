using System.Text.Json.Nodes;

namespace Codale.Core.Agents;

/// <summary>
/// A stdio MCP server the host attaches to an agent session: a built-in helper such as
/// the browser or desktop-control server. Provider-neutral - each driver renders it
/// in its own dialect (Claude's <c>--mcp-config</c> JSON).
/// </summary>
public sealed record McpServerSpec
{
    /// <summary>The server's name; its tools surface as <c>mcp__{Name}__{tool}</c>.</summary>
    public required string Name { get; init; }

    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Every tool call asks the user, whatever the permission mode says - for servers
    /// that act on the real desktop. Plan mode refuses them outright.
    /// </summary>
    public bool RequireApproval { get; init; }

    /// <summary>Calls run without asking in the working modes (auto, acceptEdits) - for servers the host itself ships and sandboxes.</summary>
    public bool AutoApprove { get; init; }

    /// <summary>
    /// Tools (bare names, e.g. <c>browser_evaluate</c>) that ask the user even though the rest
    /// of the server runs unprompted - the few that can reach beyond the sandbox.
    /// </summary>
    public IReadOnlyList<string> AskTools { get; init; } = [];

    public static string PrefixOf(string name) => $"mcp__{name}__";

    /// <summary>
    /// True when <paramref name="toolName"/> (the CLI's full <c>mcp__server__tool</c> name) belongs
    /// to an <see cref="AutoApprove"/> server and is not one of its <see cref="AskTools"/>.
    /// </summary>
    public static bool RunsUnasked(IReadOnlyList<McpServerSpec> servers, string toolName) =>
        servers.Any(s => s is { AutoApprove: true, RequireApproval: false } &&
                         toolName.StartsWith(PrefixOf(s.Name), StringComparison.Ordinal) &&
                         !s.AskTools.Contains(toolName[PrefixOf(s.Name).Length..], StringComparer.Ordinal));

    /// <summary>Standard <c>mcp_config.json</c> document for servers, recognized by Antigravity and other MCP-compliant CLIs.</summary>
    public static string? ToStandardMcpConfig(IReadOnlyList<McpServerSpec> servers) => ToAgyConfig(servers);

    /// <summary>Generates <c>mcp_config.json</c> for Antigravity, marking all tools eager so they are directly exposed.</summary>
    public static string? ToAgyConfig(IReadOnlyList<McpServerSpec> servers)
    {
        if (servers.Count == 0)
        {
            return null;
        }

        var map = new JsonObject();
        foreach (var server in servers)
        {
            var entry = new JsonObject
            {
                ["command"] = server.Command,
                ["args"] = new JsonArray(server.Args.Select(a => (JsonNode)a).ToArray()),
                ["force_all_tools_eager"] = true,
            };

            if (server.Env is { Count: > 0 } env)
            {
                var vars = new JsonObject();
                foreach (var (key, value) in env)
                {
                    vars[key] = value;
                }

                entry["env"] = vars;
            }

            map[server.Name] = entry;
        }

        return new JsonObject { ["mcpServers"] = map }.ToJsonString();
    }

    /// <summary>The <c>--mcp-config</c> document for a set of servers, or null when there are none.</summary>
    public static string? ToClaudeConfig(IReadOnlyList<McpServerSpec> servers)
    {
        if (servers.Count == 0)
        {
            return null;
        }

        var map = new JsonObject();
        foreach (var server in servers)
        {
            var entry = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = server.Command,
                ["args"] = new JsonArray(server.Args.Select(a => (JsonNode)a).ToArray()),
            };

            if (server.Env is { Count: > 0 } env)
            {
                var vars = new JsonObject();
                foreach (var (key, value) in env)
                {
                    vars[key] = value;
                }

                entry["env"] = vars;
            }

            map[server.Name] = entry;
        }

        return new JsonObject { ["mcpServers"] = map }.ToJsonString();
    }

    /// <summary>
    /// The one <c>--settings</c> document: ask rules for servers that require approval (and
    /// for individual <see cref="AskTools"/>) and the host's hooks - the shell
    /// guard being a <c>PreToolUse</c> hook that refuses background launches (see the tasks
    /// MCP exe). Null when there is nothing to say.
    /// </summary>
    public static string? ToClaudeSettings(IReadOnlyList<McpServerSpec> servers, ClaudeHookSettings hooks)
    {
        var settings = new JsonObject();
        var permissions = new JsonObject();

        // A whole-server rule already covers that server's tools, so its AskTools add nothing.
        var ask = servers.Where(s => s.RequireApproval).Select(s => $"mcp__{s.Name}")
            .Concat(servers.Where(s => !s.RequireApproval)
                .SelectMany(s => s.AskTools.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => PrefixOf(s.Name) + t.Trim())))
            .Distinct(StringComparer.Ordinal)
            .Select(rule => (JsonNode)rule)
            .ToArray();
        if (ask.Length > 0)
        {
            permissions["ask"] = new JsonArray(ask);
        }

        if (permissions.Count > 0)
        {
            settings["permissions"] = permissions;
        }

        var pre = new JsonArray();
        if (hooks.ShellGuardCommand is { Length: > 0 } guard)
        {
            pre.Add(Hook(ShellTools, guard));
        }

        var events = new JsonObject();
        if (pre.Count > 0)
        {
            events["PreToolUse"] = pre;
        }

        if (events.Count > 0)
        {
            settings["hooks"] = events;
        }

        return settings.Count == 0 ? null : settings.ToJsonString();
    }

    private const string ShellTools = "Bash|PowerShell";

    private static JsonObject Hook(string matcher, string command, int? timeoutSeconds = null)
    {
        var hook = new JsonObject { ["type"] = "command", ["command"] = command };
        if (timeoutSeconds is > 0)
        {
            hook["timeout"] = timeoutSeconds;
        }

        return new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray(hook) };
    }
}

/// <summary>The host's hooks for a Claude session, rendered into its <c>--settings</c>.</summary>
public sealed record ClaudeHookSettings
{
    /// <summary><c>PreToolUse</c> on the shell tools: refuses background launches.</summary>
    public string? ShellGuardCommand { get; init; }
}