using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.History;

/// <summary>
/// Reads Aider AI pair programming chat history from <c>.aider.chat.history.md</c> or
/// <c>.aider.input.history</c> in the project directory, allowing Aider sessions to be listed and replayed.
/// </summary>
public sealed class AiderHistoryReader : ICliHistoryReader
{
    private readonly string _projectPath;

    public AiderHistoryReader(string projectPath)
    {
        _projectPath = Path.TrimEndingDirectorySeparator(projectPath.Replace('/', '\\'));
        ChatHistoryFile = Path.Combine(_projectPath, ".aider.chat.history.md");
        InputHistoryFile = Path.Combine(_projectPath, ".aider.input.history");
    }

    public string CliId => "aider";

    /// <summary>The markdown chat history file located in the project root.</summary>
    public string ChatHistoryFile { get; }

    /// <summary>The prompt input history file located in the project root.</summary>
    public string InputHistoryFile { get; }

    public string? HistorySourcePath => File.Exists(ChatHistoryFile) ? ChatHistoryFile : InputHistoryFile;

    public bool IsSessionFile(string filePath) =>
        string.Equals(filePath, ChatHistoryFile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(filePath, InputHistoryFile, StringComparison.OrdinalIgnoreCase);

    private static readonly Regex SessionHeaderRegex = new(
        @"^# aider chat started at (?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private sealed record AiderTurn(bool IsUser, string Content, DateTimeOffset Timestamp);

    private sealed class AiderSession
    {
        public required string Id { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<AiderTurn> Turns { get; } = [];
    }

    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        var sessions = ReadSessions();
        if (sessions.Count == 0 && File.Exists(InputHistoryFile))
        {
            var fallback = ReadInputHistory();
            if (fallback is not null)
            {
                sessions.Add(fallback);
            }
        }

        return sessions
            .Select(s =>
            {
                var userPrompts = s.Turns.Where(t => t.IsUser).Select(t => t.Content).ToList();
                return new TranscriptSummary
                {
                    SessionId = s.Id,
                    FilePath = File.Exists(ChatHistoryFile) ? ChatHistoryFile : InputHistoryFile,
                    StartedAt = s.StartedAt,
                    UpdatedAt = s.UpdatedAt,
                    UserTurns = userPrompts.Count,
                    FirstPrompt = userPrompts.FirstOrDefault(),
                    LastPrompt = userPrompts.LastOrDefault(),
                };
            })
            .Where(s => s.UserTurns > 0)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    public IEnumerable<AgentEvent> Replay(string sessionId)
    {
        var sessions = ReadSessions();
        var match = sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));

        if (match is null && File.Exists(InputHistoryFile))
        {
            var fallback = ReadInputHistory();
            if (fallback is not null && string.Equals(fallback.Id, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                match = fallback;
            }
        }

        if (match is null)
        {
            return [];
        }

        var events = new List<AgentEvent>();
        foreach (var turn in match.Turns)
        {
            if (turn.IsUser)
            {
                events.Add(new UserMessageRecorded(turn.Content) { Timestamp = turn.Timestamp });
            }
            else
            {
                events.Add(new AssistantMessageCompleted
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    Model = "aider",
                    Text = turn.Content,
                    Timestamp = turn.Timestamp,
                });
            }
        }

        return events;
    }

    private List<AiderSession> ReadSessions()
    {
        var result = new List<AiderSession>();
        if (!File.Exists(ChatHistoryFile))
        {
            return result;
        }

        try
        {
            var text = File.ReadAllText(ChatHistoryFile);
            var matches = SessionHeaderRegex.Matches(text);

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var startTimeStr = match.Groups["time"].Value;
                var startTime = DateTimeOffset.TryParseExact(
                    startTimeStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;

                var startIdx = match.Index + match.Length;
                var endIdx = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
                var block = text.Substring(startIdx, endIdx - startIdx);

                var session = new AiderSession
                {
                    Id = $"aider-{startTime:yyyyMMdd-HHmmss}",
                    StartedAt = startTime,
                    UpdatedAt = startTime,
                };

                ParseTurns(block, session);
                result.Add(session);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static void ParseTurns(string block, AiderSession session)
    {
        var lines = block.Split('\n');
        var currentUserPrompt = new StringBuilder();
        var currentAssistant = new StringBuilder();
        var inUser = false;
        var currentTime = session.StartedAt;

        void FlushUser()
        {
            if (currentUserPrompt.Length > 0)
            {
                var content = currentUserPrompt.ToString().Trim();
                if (content.Length > 0)
                {
                    session.Turns.Add(new AiderTurn(true, content, currentTime));
                }
                currentUserPrompt.Clear();
            }
        }

        void FlushAssistant()
        {
            if (currentAssistant.Length > 0)
            {
                var content = currentAssistant.ToString().Trim();
                if (content.Length > 0)
                {
                    session.Turns.Add(new AiderTurn(false, content, currentTime));
                }
                currentAssistant.Clear();
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                FlushAssistant();
                FlushUser();
                inUser = true;
                currentUserPrompt.AppendLine(line.Substring(5).Trim());
            }
            else if (inUser)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inUser = false;
                    FlushUser();
                }
                else if (line.StartsWith("```", StringComparison.Ordinal) ||
                    line.StartsWith("I'll ", StringComparison.Ordinal) ||
                    line.StartsWith("I will ", StringComparison.Ordinal) ||
                    line.StartsWith("Applied edit to", StringComparison.Ordinal) ||
                    line.StartsWith("Commit ", StringComparison.Ordinal))
                {
                    inUser = false;
                    FlushUser();
                    currentAssistant.AppendLine(line);
                }
                else
                {
                    currentUserPrompt.AppendLine(line);
                }
            }
            else
            {
                currentAssistant.AppendLine(line);
            }
        }

        FlushUser();
        FlushAssistant();

        if (session.Turns.Count > 0)
        {
            session.UpdatedAt = session.StartedAt.AddMinutes(session.Turns.Count);
        }
    }

    private AiderSession? ReadInputHistory()
    {
        try
        {
            var lines = File.ReadAllLines(InputHistoryFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .ToList();

            if (lines.Count == 0)
            {
                return null;
            }

            var info = new FileInfo(InputHistoryFile);
            var session = new AiderSession
            {
                Id = "aider-input-history",
                StartedAt = new DateTimeOffset(info.CreationTimeUtc),
                UpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc),
            };

            foreach (var line in lines)
            {
                session.Turns.Add(new AiderTurn(true, line, session.UpdatedAt));
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
