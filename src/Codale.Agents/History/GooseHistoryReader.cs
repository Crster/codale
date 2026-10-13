using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.History;

/// <summary>
/// Reads Block Goose CLI session files (<c>&lt;session-id&gt;.json</c>) stored in Goose's
/// application data directory, allowing past sessions to be browsed and replayed.
/// </summary>
public sealed class GooseHistoryReader : ICliHistoryReader
{
    private readonly string _projectPath;
    private readonly string? _sessionsDirectory;

    public GooseHistoryReader(string projectPath, string? sessionsDir = null)
    {
        _projectPath = Normalize(projectPath);
        _sessionsDirectory = sessionsDir ?? ResolveSessionsDirectory();
    }

    public string CliId => "goose";

    public string? HistorySourcePath => _sessionsDirectory;

    public bool IsSessionFile(string filePath) =>
        _sessionsDirectory is not null &&
        filePath.StartsWith(_sessionsDirectory, StringComparison.OrdinalIgnoreCase) &&
        filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    private static string? ResolveSessionsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new[]
        {
            Path.Combine(localAppData, "block", "goose", "sessions"),
            Path.Combine(userProfile, ".local", "share", "goose", "sessions"),
            Path.Combine(userProfile, ".config", "goose", "sessions"),
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        if (string.IsNullOrWhiteSpace(_sessionsDirectory) || !Directory.Exists(_sessionsDirectory))
        {
            return [];
        }

        var summaries = new List<TranscriptSummary>();

        foreach (var file in Directory.EnumerateFiles(_sessionsDirectory, "*.json"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var workingDir = Str(root, "working_dir") ?? Str(root, "workspace") ?? Str(root, "cwd");
                if (workingDir is not null && Normalize(workingDir) != _projectPath)
                {
                    continue;
                }

                var sessionId = Str(root, "id") ?? Path.GetFileNameWithoutExtension(file);
                var createdAt = ParseDate(root, "created_at") ?? ParseDate(root, "timestamp") ?? File.GetCreationTimeUtc(file);
                var updatedAt = ParseDate(root, "updated_at") ?? File.GetLastWriteTimeUtc(file);

                var userPrompts = new List<string>();
                if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msgs.EnumerateArray())
                    {
                        if (Str(m, "role") == "user")
                        {
                            var content = ExtractContent(m);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                userPrompts.Add(content);
                            }
                        }
                    }
                }

                if (userPrompts.Count > 0)
                {
                    summaries.Add(new TranscriptSummary
                    {
                        SessionId = sessionId,
                        FilePath = file,
                        StartedAt = createdAt,
                        UpdatedAt = updatedAt,
                        UserTurns = userPrompts.Count,
                        FirstPrompt = userPrompts[0],
                        LastPrompt = userPrompts[^1],
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public IEnumerable<AgentEvent> Replay(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_sessionsDirectory) || !Directory.Exists(_sessionsDirectory))
        {
            return [];
        }

        var targetFile = Path.Combine(_sessionsDirectory, sessionId.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? sessionId : sessionId + ".json");
        if (!File.Exists(targetFile))
        {
            targetFile = Directory.EnumerateFiles(_sessionsDirectory, "*.json")
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(sessionId, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        if (!File.Exists(targetFile))
        {
            return [];
        }

        var events = new List<AgentEvent>();
        try
        {
            using var stream = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    var role = Str(m, "role");
                    var at = ParseDate(m, "created_at") ?? ParseDate(m, "timestamp") ?? DateTimeOffset.UtcNow;
                    var content = ExtractContent(m);

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    if (role == "user")
                    {
                        events.Add(new UserMessageRecorded(content) { Timestamp = at });
                    }
                    else if (role == "assistant")
                    {
                        events.Add(new AssistantMessageCompleted
                        {
                            MessageId = Guid.NewGuid().ToString("N"),
                            Model = "goose",
                            Text = content,
                            Timestamp = at,
                        });
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return events;
    }

    private static string? ExtractContent(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var contentElem))
        {
            return null;
        }

        if (contentElem.ValueKind == JsonValueKind.String)
        {
            return contentElem.GetString();
        }

        if (contentElem.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in contentElem.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    parts.Add(part.GetString()!);
                }
                else if (part.ValueKind == JsonValueKind.Object && Str(part, "type") == "text")
                {
                    var text = Str(part, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ParseDate(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dto))
        {
            return dto;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ts))
        {
            return ts > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ts) : DateTimeOffset.FromUnixTimeSeconds(ts);
        }

        return null;
    }
}
