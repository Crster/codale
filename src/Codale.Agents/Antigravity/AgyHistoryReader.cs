using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Antigravity;

/// <summary>
/// Lists Antigravity CLI conversations from its prompt log (<c>history.jsonl</c> in the CLI's app
/// data folder), so the session panel's history can show them while <c>agy</c> runs in a terminal.
/// </summary>
/// <remarks>
/// The conversations themselves are SQLite files holding protobuf blobs, which are not read here:
/// a replayed conversation shows what the user typed, not the agent's answers.
/// </remarks>
public sealed class AgyHistoryReader
{
    private readonly string _projectPath;

    public AgyHistoryReader(string projectPath, string? agyHome = null)
    {
        _projectPath = Normalize(projectPath);
        var home = agyHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
        HistoryFile = Path.Combine(home, "history.jsonl");
    }

    /// <summary>The prompt log; stands in for the transcript file of every summary.</summary>
    public string HistoryFile { get; }

    private readonly record struct Entry(string Text, DateTimeOffset At, bool IsCommand);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    /// <summary>This project's conversations, newest first. Conversations with no typed prompt are skipped.</summary>
    public IReadOnlyList<TranscriptSummary> ListSessions() =>
        ReadEntries()
            .Select(e => (Id: e.Key, Entries: e.Value, Prompts: e.Value.Where(x => !x.IsCommand).ToList()))
            .Where(e => e.Prompts.Count > 0)
            .Select(e => new TranscriptSummary
            {
                SessionId = e.Id,
                FilePath = HistoryFile,
                StartedAt = e.Entries[0].At,
                UpdatedAt = e.Entries[^1].At,
                UserTurns = e.Prompts.Count,
                FirstPrompt = e.Prompts[0].Text,
                LastPrompt = e.Prompts[^1].Text,
            })
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    /// <summary>The prompts of one conversation as events the chat panel already renders.</summary>
    public IEnumerable<AgentEvent> Replay(string sessionId) =>
        ReadEntries().TryGetValue(sessionId, out var entries)
            ? entries.Where(e => !e.IsCommand).Select(e => (AgentEvent)new UserMessageRecorded(e.Text) { Timestamp = e.At }).ToList()
            : [];

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
