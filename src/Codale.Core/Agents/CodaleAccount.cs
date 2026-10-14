namespace Codale.Core.Agents;

/// <summary>
/// Codale signs in to Claude and Codex on its own: every CLI process it starts for a chat,
/// the helper or the model catalogue keeps its login, settings and session history in
/// folders under Codale's data directory instead of the CLI's default (<c>~/.claude</c>,
/// <c>~/.codex</c>). A CLI run directly in a terminal keeps the default account, so a
/// work login in Codale and a personal one in the terminal never meet.
/// </summary>
public static class CodaleAccount
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "accounts");

    /// <summary>The folder passed to the Claude CLI as <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static string ClaudeHome => Ensure(Path.Combine(Root, "claude"));

    /// <summary>The folder passed to the Codex CLI as <c>CODEX_HOME</c>; codex refuses to start when it is missing.</summary>
    public static string CodexHome => Ensure(Path.Combine(Root, "codex"));

    public const string ClaudeVariable = "CLAUDE_CONFIG_DIR";

    public const string CodexVariable = "CODEX_HOME";

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
