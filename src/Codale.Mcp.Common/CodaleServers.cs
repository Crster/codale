namespace Codale.Mcp;

/// <summary>
/// Codale's own workspace servers. One exe (Codale.Mcp.Tasks) runs as each of them, picked by
/// <c>--server NAME</c>, so every server's name says what its tools are for.
/// </summary>
public static class CodaleServers
{
    /// <summary>The session panel: todos_set (the step list) and artifact_add.</summary>
    public const string Panel = "codale-panel";

    /// <summary>The project's saved run commands (the commands menu); run_command runs one in a terminal tab.</summary>
    public const string Command = "codale-command";

    /// <summary>The workspace's terminal tabs: read them, run a command in a new one, stop one the agent opened.</summary>
    public const string Terminal = "codale-terminal";

    /// <summary>Notes remembered for the workspace.</summary>
    public const string Memory = "codale-memory";

    /// <summary>Secrets stored for the workspace.</summary>
    public const string Secret = "codale-secret";

    public static IReadOnlyList<string> All { get; } = [Panel, Command, Terminal, Memory, Secret];

    /// <summary>The server a tool belongs to; null for a tool none of them has.</summary>
    public static string? Of(string tool) => tool switch
    {
        "todos_set" or "artifact_add" => Panel,
        "list_commands" or "add_command" or "run_command" => Command,
        "list_terminals" or "read_terminal" or "run_in_terminal" or "stop_terminal" => Terminal,
        "memory_save" or "memory_search" or "memory_list" or "memory_forget" => Memory,
        "env_list" or "env_get" or "env_set" => Secret,
        _ => null,
    };

    /// <summary>Tools that ask the user even though the rest of their server runs unprompted.</summary>
    public static IReadOnlyList<string> AskTools(string server) => server switch
    {
        Command => ["run_command", "add_command"],
        Terminal => ["run_in_terminal"],
        _ => [],
    };

    /// <summary>The instructions a server sends at connect, for a CLI in a terminal.</summary>
    public static string? Instructions(string server) => server switch
    {
        Panel => McpInstructions.Panel,
        Command => McpInstructions.Command,
        Terminal => McpInstructions.Terminal,
        Memory => McpInstructions.Memory,
        Secret => McpInstructions.Secret,
        _ => null,
    };
}
