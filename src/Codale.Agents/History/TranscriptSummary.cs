using Codale.Core.Text;

namespace Codale.Agents.History;

/// <summary>Summary of one row of the history list: a stored conversation, or a feature.</summary>
public sealed record TranscriptSummary
{
    public required string SessionId { get; init; }
    public required string FilePath { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int UserTurns { get; init; }
    public string? CustomTitle { get; init; }
    public string? FirstPrompt { get; init; }
    public string? LastPrompt { get; init; }
    public string? GitBranch { get; init; }

    /// <summary>A line under the title in place of the turn count: what a feature holds, for instance.</summary>
    public string? Detail { get; init; }

    /// <summary>What the browser shows: the user's own title, else the opening prompt.</summary>
    public string Title
    {
        get
        {
            var text = CustomTitle ?? FirstPrompt ?? LastPrompt;

            if (string.IsNullOrWhiteSpace(text))
            {
                return "(empty session)";
            }

            var single = text.ReplaceLineEndings(" ").Trim();
            return single.Length > 90 ? TextClip.Truncate(single, 90) + "…" : single;
        }
    }
}
