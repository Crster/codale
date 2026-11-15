namespace Codale.Core.Agents;

/// <summary>
/// The text Codale writes for the CLIs' MCP configuration: the Codex launch overrides, the Copilot config, the
/// per-launch environment wrapper, and the directive that tells the agent how to use the tools it was given.
/// Pure: the caller supplies the servers, the feature choices and the product's server names.
/// </summary>
public static class McpConfigText
{
    public static string CopilotConfig(IReadOnlyList<McpServerSpec> servers)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(McpServerSpec.ToClaudeConfig(servers)!)!.AsObject();
        foreach (var (_, entry) in doc["mcpServers"]!.AsObject())
        {
            entry!["type"] = "local";
            entry["tools"] = new System.Text.Json.Nodes.JsonArray("*");
        }

        return doc.ToJsonString();
    }

    public static string CodexOverrides(bool powershell, IReadOnlyList<McpServerSpec> servers)
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

    /// <summary>The Browser Use and Computer Use choices as Codex <c>-c features.*</c> overrides, each with a leading space.</summary>
    public static string CodexFeatureOverrides(bool powershell, IReadOnlyDictionary<string, bool> features) =>
        string.Concat(features.Select(f => $" -c {Quote(powershell, $"features.{f.Key}={(f.Value ? "true" : "false")}")}"));

    public static string Quote(bool powershell, string value) => powershell
        ? $"'{value.Replace("'", "''")}'"
        : $"\"{value}\"";

    public static string WithEnv(bool powershell, string name, string value, string command) => powershell
        ? $"& {{ $env:{name}={Quote(true, value)}; try {{ {command} }} finally {{ Remove-Item Env:{name} -ErrorAction SilentlyContinue }} }}"
        : $"set \"{name}={value}\" && {command} & set \"{name}=\"";

    public static string? Directive(IReadOnlyList<McpServerSpec> servers, string browserName, string computerName, string phoneName)
    {
        var parts = new List<string>();

        if (servers.Any(s => s.Name == browserName))
        {
            parts.Add(
                $"The {McpServerSpec.PrefixOf(browserName)}* tools drive Codale's own Chromium (not the user's; hidden, keeps logins). " +
                "After changing something a browser can show, verify it before reporting done: browser_navigate, browser_snapshot (prefer it " +
                "over screenshots; its refs like e12 expire at the next snapshot), exercise the flow, check browser_console_messages. " +
                "For a sign-in, captcha or 2FA call browser_handoff; never ask for credentials. Layouts: browser_set_viewport or " +
                "browser_responsive_check; dark mode: browser_emulate.");
        }

        if (servers.Any(s => s.Name == computerName))
        {
            parts.Add(
                $"You can also see and control the user's Windows desktop with the {McpServerSpec.PrefixOf(computerName)}* tools, for what " +
                "the browser tools cannot reach: native apps, installers, system dialogs, the running build of a desktop app. They act on the " +
                "real screen, so take a computer_screenshot before every click (coordinates refer to the latest screenshot) and the user approves each action.");
        }

        if (servers.Any(s => s.Name == phoneName))
        {
            parts.Add(
                $"You can control an Android phone or emulator over adb with the {McpServerSpec.PrefixOf(phoneName)}* tools, to build and test " +
                $"mobile apps. MANDATORY: NEVER execute raw 'adb' commands via terminal or shell tools. Always use the {phoneName} tools instead. " +
                "Call phone_devices first, and take a phone_screenshot or phone_ui_tree before every tap or swipe (coordinates are " +
                "device pixels on the latest view). Build with the shell (e.g. gradlew installDebug; use run_in_terminal for long-running builds or " +
                "dev servers), install with phone_install, start the app with phone_launch_app and read phone_logcat for crashes before you " +
                "report the work as done. phone_port_forward (reverse) lets the phone reach a dev server on this computer.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
