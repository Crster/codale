using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.History;

/// <summary>
/// General-purpose reader for CLI history logs recorded as JSONL files or line-delimited
/// prompt logs (used by Gemini Code Assist, Qwen Code, GitHub Copilot, Codex, etc.).
/// </summary>
public sealed class JsonlPromptHistoryReader : ICliHistoryReader
{
    private readonly string _projectPath;
    private readonly string _cliId;
    private readonly string[] _candidateFiles;

    public JsonlPromptHistoryReader(string cliId, string projectPath, IEnumerable<string> candidateFiles)
    {
        _cliId = cliId;
        _projectPath = Normalize(projectPath);
        _candidateFiles = candidateFiles.ToArray();
    }

    public string CliId => _cliId;

    public string? HistorySourcePath => _candidateFiles.FirstOrDefault(File.Exists) ?? _candidateFiles.FirstOrDefault();

    public bool IsSessionFile(string filePath) =>
        _candidateFiles.Any(c => string.Equals(c, filePath, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    private readonly record struct Entry(string Text, DateTimeOffset At);

    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        var activeFile = _candidateFiles.FirstOrDefault(File.Exists);
        if (activeFile is null)
        {
            return [];
        }

        var map = ReadFile(activeFile);
        return map
            .Where(kvp => kvp.Value.Count > 0)
            .Select(kvp => new TranscriptSummary
            {
                SessionId = kvp.Key,
                FilePath = activeFile,
                StartedAt = kvp.Value[0].At,
                UpdatedAt = kvp.Value[^1].At,
                UserTurns = kvp.Value.Count,
                FirstPrompt = kvp.Value[0].Text,
                LastPrompt = kvp.Value[^1].Text,
            })
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    public IEnumerable<AgentEvent> Replay(string sessionId)
    {
        var activeFile = _candidateFiles.FirstOrDefault(File.Exists);
        if (activeFile is null)
        {
            return [];
        }

        var map = ReadFile(activeFile);
        return map.TryGetValue(sessionId, out var entries)
            ? entries.Select(e => (AgentEvent)new UserMessageRecorded(e.Text) { Timestamp = e.At }).ToList()
            : [];
    }

    private Dictionary<string, List<Entry>> ReadFile(string path)
    {
        var map = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lineIndex = 0;

            while (reader.ReadLine() is { } line)
            {
                lineIndex++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        var workspace = Str(root, "workspace") ?? Str(root, "cwd") ?? Str(root, "project");
                        if (workspace is not null && Normalize(workspace) != _projectPath)
                        {
                            continue;
                        }

                        var sessionId = Str(root, "conversationId") ?? Str(root, "sessionId") ?? Str(root, "id") ?? $"session-{Path.GetFileNameWithoutExtension(path)}";
                        var prompt = Str(root, "display") ?? Str(root, "prompt") ?? Str(root, "text") ?? Str(root, "command") ?? Str(root, "content");

                        if (string.IsNullOrWhiteSpace(prompt))
                        {
                            continue;
                        }

                        var at = ParseDate(root, "timestamp") ?? ParseDate(root, "created_at") ?? ParseDate(root, "at") ?? DateTimeOffset.UtcNow;

                        if (!map.TryGetValue(sessionId, out var list))
                        {
                            map[sessionId] = list = [];
                        }

                        list.Add(new Entry(prompt, at));
                    }
                    catch (JsonException)
                    {
                    }
                }
                else
                {
                    // Plain text prompt line
                    var sessionId = $"{_cliId}-{File.GetLastWriteTimeUtc(path):yyyyMMdd}";
                    if (!map.TryGetValue(sessionId, out var list))
                    {
                        map[sessionId] = list = [];
                    }

                    list.Add(new Entry(line.Trim(), File.GetLastWriteTimeUtc(path)));
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
