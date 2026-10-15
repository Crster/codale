using System.Text.Json;

namespace Codale.Agents.History;

/// <summary>
/// Lists the Antigravity CLI's conversations for a project from its prompt log
/// (<c>history.jsonl</c> in the CLI's app data folder); <c>agy --conversation &lt;id&gt;</c> resumes one.
/// </summary>
public sealed class AgyHistoryReader : ICliHistoryReader
{
    private readonly string _projectPath;

    public AgyHistoryReader(string projectPath, string? agyHome = null)
    {
        _projectPath = Normalize(projectPath);
        HistoryFile = Path.Combine(
            agyHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli"),
            "history.jsonl");
    }

    public string CliId => "agy";

    /// <summary>The prompt log; watched by the host to detect when new turns are logged.</summary>
    public string HistoryFile { get; }

    public string? HistorySourcePath => HistoryFile;

    public bool IsSessionFile(string filePath) =>
        string.Equals(filePath, HistoryFile, StringComparison.OrdinalIgnoreCase);

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

                    var at = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms)
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