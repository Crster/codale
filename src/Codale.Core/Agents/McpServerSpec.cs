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
    /// for individual <see cref="AskTools"/>), deny rules, and the host's hooks - the shell
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

        var deny = hooks.DenyRules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => (JsonNode)r.Trim()).ToArray();
        if (deny.Length > 0)
        {
            permissions["deny"] = new JsonArray(deny);
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

        if (hooks.ReadGuardCommand is { Length: > 0 } readGuard)
        {
            pre.Add(Hook("Read", readGuard));
        }

        var post = new JsonArray();
        if (hooks.ShellOutputCommand is { Length: > 0 } output)
        {
            post.Add(Hook(ShellTools, output, hooks.ShellOutputTimeoutSeconds));
        }

        var events = new JsonObject();
        if (pre.Count > 0)
        {
            events["PreToolUse"] = pre;
        }

        if (post.Count > 0)
        {
            events["PostToolUse"] = post;
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

/// <summary>The host's hooks and deny rules for a Claude session, rendered into its <c>--settings</c>.</summary>
public sealed record ClaudeHookSettings
{
    /// <summary><c>PreToolUse</c> on the shell tools: refuses background launches.</summary>
    public string? ShellGuardCommand { get; init; }

    /// <summary><c>PreToolUse</c> on Read: turns away a first whole-file read of a large file.</summary>
    public string? ReadGuardCommand { get; init; }

    /// <summary><c>PostToolUse</c> on the shell tools: condenses long output before the model reads it.</summary>
    public string? ShellOutputCommand { get; init; }

    /// <summary>How long the CLI waits for the output hook, which may ask a model for a digest.</summary>
    public int ShellOutputTimeoutSeconds { get; init; } = 45;

    /// <summary>Permission rules the CLI refuses outright, e.g. <c>Read(**/bin/**)</c>.</summary>
    public IReadOnlyList<string> DenyRules { get; init; } = [];
}
