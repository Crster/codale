using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.History;

/// <summary>
/// Common interface for reading and replaying historical conversation sessions
/// recorded by AI coding agent CLIs running in Codale or externally.
/// </summary>
public interface ICliHistoryReader
{
    /// <summary>The identifier of the CLI this reader supports (e.g. "agy", "aider", "goose", "gemini").</summary>
    string CliId { get; }

    /// <summary>
    /// Path to the file or directory that receives new history entries; watched by the host
    /// to refresh the session list automatically when the CLI logs new turns.
    /// </summary>
    string? HistorySourcePath { get; }

    /// <summary>True if the given file path belongs to this reader's stored sessions.</summary>
    bool IsSessionFile(string filePath);

    /// <summary>Lists recorded sessions for the target project, sorted by most recently updated first.</summary>
    IReadOnlyList<TranscriptSummary> ListSessions();

    /// <summary>Replays the turns of a session as <see cref="AgentEvent"/> instances that Codale's UI can render.</summary>
    IEnumerable<AgentEvent> Replay(string sessionId);
}
