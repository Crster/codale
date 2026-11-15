namespace Codale.Agents.History;

public interface ICliHistoryReaderFactory
{
    ICliHistoryReader? Create(string? cliId, string projectPath, string? home = null);
}

/// <summary>Resolves provider-owned transcripts for a workspace and credential home.</summary>
public sealed class CliHistoryReaderFactory : ICliHistoryReaderFactory
{
    public ICliHistoryReader? Create(string? cliId, string projectPath, string? home = null) => cliId?.ToLowerInvariant() switch
    {
        "claude" => new ClaudeSessionHistoryReader(projectPath, home),
        "codex" => new CodexSessionHistoryReader(projectPath, home),
        _ => null,
    };
}
