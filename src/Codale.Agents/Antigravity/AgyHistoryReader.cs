using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Agents.History;
using Codale.Core.Agents;

namespace Codale.Agents.Antigravity;

/// <summary>
/// Lists Antigravity CLI conversations from its prompt log (<c>history.jsonl</c> in the CLI's app
/// data folder) or full conversation transcripts (<c>brain/&lt;conversation-id&gt;/.system_generated/logs/transcript.jsonl</c>),
/// allowing past sessions to be browsed and replayed natively in Codale.
/// </summary>
public sealed class AgyHistoryReader : ICliHistoryReader
{
    private readonly string _projectPath;
    private readonly string _agyHome;

    public AgyHistoryReader(string projectPath, string? agyHome = null)
    {
        _projectPath = Normalize(projectPath);
        _agyHome = agyHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
        HistoryFile = Path.Combine(_agyHome, "history.jsonl");
    }

    public string CliId => "agy";

    /// <summary>The prompt log; watched by the host to detect when new turns are logged.</summary>
    public string HistoryFile { get; }

    public string? HistorySourcePath => HistoryFile;

    public bool IsSessionFile(string filePath) =>
        string.Equals(filePath, HistoryFile, StringComparison.OrdinalIgnoreCase) ||
        filePath.Replace('\\', '/').Contains("/.system_generated/logs/transcript.jsonl");

    private readonly record struct Entry(string Text, DateTimeOffset At, bool IsCommand);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    /// <summary>This project's conversations, newest first. Conversations with no typed prompt are skipped.</summary>
    public IReadOnlyList<TranscriptSummary> ListSessions() =>
        ReadEntries()
            .Select(e =>
            {
                var prompts = e.Value.Where(x => !x.IsCommand).ToList();
                var transcriptPath = TranscriptPath(e.Key);
                var targetFile = File.Exists(transcriptPath) ? transcriptPath : HistoryFile;

                return (Id: e.Key, Entries: e.Value, Prompts: prompts, FilePath: targetFile);
            })
            .Where(e => e.Prompts.Count > 0)
            .Select(e => new TranscriptSummary
            {
                SessionId = e.Id,
                FilePath = e.FilePath,
                StartedAt = e.Entries[0].At,
                UpdatedAt = e.Entries[^1].At,
                UserTurns = e.Prompts.Count,
                FirstPrompt = e.Prompts[0].Text,
                LastPrompt = e.Prompts[^1].Text,
            })
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    /// <summary>The path to an Antigravity conversation's full JSONL transcript, if written.</summary>
    private string TranscriptPath(string sessionId) =>
        Path.Combine(_agyHome, "brain", sessionId, ".system_generated", "logs", "transcript.jsonl");

    /// <summary>Replays a conversation, using the detailed transcript when available or prompt history as fallback.</summary>
    public IEnumerable<AgentEvent> Replay(string sessionId)
    {
        var transcriptPath = TranscriptPath(sessionId);
        if (File.Exists(transcriptPath))
        {
            var events = ReplayTranscript(transcriptPath);
            if (events.Count > 0)
            {
                return events;
            }
        }

        return ReadEntries().TryGetValue(sessionId, out var entries)
            ? entries.Where(e => !e.IsCommand).Select(e => (AgentEvent)new UserMessageRecorded(e.Text) { Timestamp = e.At }).ToList()
            : [];
    }

    private static IReadOnlyList<AgentEvent> ReplayTranscript(string path)
    {
        var events = new List<AgentEvent>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = Str(root, "type");
                    var at = root.TryGetProperty("created_at", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var parsed)
                        ? parsed
                        : DateTimeOffset.UtcNow;

                    if (type == "USER_INPUT")
                    {
                        var content = Str(root, "content") ?? "";
                        var prompt = ExtractUserRequest(content);
                        if (!string.IsNullOrWhiteSpace(prompt))
                        {
                            events.Add(new UserMessageRecorded(prompt) { Timestamp = at });
                        }
                    }
                    else if (type == "PLANNER_RESPONSE")
                    {
                        var content = Str(root, "content");
                        var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(thinking))
                        {
                            events.Add(new AssistantMessageCompleted
                            {
                                MessageId = Str(root, "step_index") ?? Guid.NewGuid().ToString("N"),
                                Model = "antigravity",
                                Text = content ?? "",
                                HasThinking = !string.IsNullOrWhiteSpace(thinking),
                                Thinking = thinking,
                                Timestamp = at,
                            });
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return events;
    }

    private static string ExtractUserRequest(string raw)
    {
        const string openTag = "<USER_REQUEST>";
        const string closeTag = "</USER_REQUEST>";
        var start = raw.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = raw.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
            if (end > start)
            {
                return raw.Substring(start + openTag.Length, end - start - openTag.Length).Trim();
            }
        }

        return raw.Trim();
    }

    private Dictionary<string, List<Entry>> ReadEntries()
    {
        var map = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        if (!File.Exists(HistoryFile))
        {
            return map;
        }

        try
        {
            using var stream = new FileStream(HistoryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        Str(root, "conversationId") is not { Length: > 0 } conversation ||
                        Str(root, "workspace") is not { } folder || Normalize(folder) != _projectPath ||
                        Str(root, "display") is not { } text)
                    {
                        continue;
                    }

                    var at = root.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : default;

                    if (!map.TryGetValue(conversation, out var list))
                    {
                        map[conversation] = list = [];
                    }

                    list.Add(new Entry(text, at, Str(root, "type") == "slash_command"));
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return map;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
