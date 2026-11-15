namespace Codale.Core.Agents;

/// <summary>How an agent gets one kind of tool (browser, desktop, phone).</summary>
public enum AgentToolMode
{
    /// <summary>Neither Codale's server nor the CLI's own equivalent.</summary>
    Block,

    /// <summary>Whatever the CLI itself brings (for Claude's browser: Claude in Chrome); Codale's server stays out.</summary>
    CliDefault,

    /// <summary>Codale's own MCP server.</summary>
    Allow,
}

/// <summary>
/// The per-CLI decisions that follow from the browser, desktop and phone tool modes: which of the CLI's own tools
/// are on or off, which Codex feature flags carry the choice, and the key a session uses to notice a change.
/// Pure: the settings class supplies the modes and whether Codale's servers are available.
/// </summary>
public static class McpToolPolicy
{
    /// <summary>Claude's own browser server name (Claude in Chrome).</summary>
    public const string ClaudeChromeServer = "claude-in-chrome";

    /// <summary>Claude's own desktop control server name.</summary>
    public const string ClaudeComputerServer = "computer-use";

    /// <summary>Codex's feature flags behind its own browser.</summary>
    public static readonly IReadOnlyList<string> CodexBrowserFeatures = ["browser_use", "browser_use_external", "in_app_browser"];

    /// <summary>Codex's feature flag behind its own desktop control.</summary>
    public const string CodexComputerFeature = "computer_use";

    /// <summary>Stored values; "1" and "0" are what the old on/off toggles wrote.</summary>
    public static AgentToolMode ParseMode(string? stored, AgentToolMode fallback) => stored switch
    {
        "allow" or "1" => AgentToolMode.Allow,
        "cli" => AgentToolMode.CliDefault,
        "block" or "0" => AgentToolMode.Block,
        _ => fallback,
    };

    public static string FormatMode(AgentToolMode mode) => mode switch
    {
        AgentToolMode.Allow => "allow",
        AgentToolMode.CliDefault => "cli",
        _ => "block",
    };

    /// <summary>
    /// The CLI's own tool for a mode: on for CLI Default, off for Block, and off for Allow so the agent has one
    /// of each kind - Codale's. Null leaves the CLI's own setting alone (Allow without Codale's server).
    /// </summary>
    public static bool? Native(AgentToolMode mode, bool codaleAvailable) => mode switch
    {
        AgentToolMode.CliDefault => true,
        AgentToolMode.Allow when !codaleAvailable => null,
        _ => false,
    };

    /// <summary>Claude in Chrome: <c>--chrome</c> / <c>--no-chrome</c>.</summary>
    public static bool? ClaudeChrome(AgentToolMode browserMode, bool browserAvailable) => Native(browserMode, browserAvailable);

    /// <summary>Codex feature flags (<c>-c features.NAME=bool</c>) that carry the Browser Use and Computer Use choices.</summary>
    public static IReadOnlyDictionary<string, bool> CodexFeatures(AgentToolMode browserMode, bool browserAvailable,
        AgentToolMode computerMode, bool computerAvailable)
    {
        var features = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (Native(browserMode, browserAvailable) is { } browser)
        {
            foreach (var name in CodexBrowserFeatures)
            {
                features[name] = browser;
            }
        }

        if (Native(computerMode, computerAvailable) is { } computer)
        {
            features[CodexComputerFeature] = computer;
        }

        return features;
    }

    /// <summary>The Claude CLI's own tools a Block setting takes away (comma-separated), or null.</summary>
    public static string? ClaudeDisallowedTools(AgentToolMode browserMode, AgentToolMode computerMode)
    {
        var blocked = new List<string>();
        if (browserMode == AgentToolMode.Block)
        {
            blocked.Add($"mcp__{ClaudeChromeServer}");
        }

        if (computerMode == AgentToolMode.Block)
        {
            blocked.Add($"mcp__{ClaudeComputerServer}");
        }

        return blocked.Count == 0 ? null : string.Join(',', blocked);
    }

    /// <summary>The three modes in one string, so a session can tell when they changed since it started.</summary>
    public static string ModesKey(AgentToolMode browserMode, AgentToolMode computerMode, AgentToolMode phoneMode) =>
        $"{FormatMode(browserMode)}|{FormatMode(computerMode)}|{FormatMode(phoneMode)}";
}