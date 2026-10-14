namespace Codale.Agents.History;

/// <summary>
/// How each CLI picks a stored conversation back up natively. Only CLIs that take a session id
/// on the command line are listed: for the rest there is nothing reliable to resume by.
/// </summary>
public static class CliResume
{
    /// <summary>True when the CLI can be resumed by session id.</summary>
    public static bool IsSupported(string? cliId) => Arguments(cliId, "x") is not null;

    /// <summary>The arguments after the CLI's name that resume <paramref name="sessionId"/>, or null.</summary>
    public static string? Arguments(string? cliId, string sessionId) => cliId?.ToLowerInvariant() switch
    {
        "claude" => $"--resume {sessionId}",
        "codex" => $"resume {sessionId}",
        "copilot" => $"--resume {sessionId}",
        "opencode" => $"--session {sessionId}",
        "agy" => $"--conversation {sessionId}",
        _ => null,
    };

    /// <summary>
    /// A history reader that carries session ids for the CLI, or null when none exists for it.
    /// These feed the terminal session panel's own list.
    /// </summary>
    public static ICliHistoryReader? CreateReader(string? cliId, string projectPath) => cliId?.ToLowerInvariant() switch
    {
        "claude" => new ClaudeSessionHistoryReader(projectPath),
        "codex" => new CodexSessionHistoryReader(projectPath),
        "agy" => new AgyHistoryReader(projectPath),
        _ => null,
    };
}
