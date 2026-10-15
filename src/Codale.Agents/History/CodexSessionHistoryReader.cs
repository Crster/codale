using System.Text.Json;

using Codale.Core.Text;

namespace Codale.Agents.History;

/// <summary>
/// Lists the Codex CLI's stored conversations for a project: rollout files under
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, whose first record names the
/// working directory and the id that <c>codex resume</c> takes.
/// </summary>
public sealed class CodexSessionHistoryReader(string projectPath) : ICliHistoryReader
{
    private const int MaxFiles = 120;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), TranscriptSummary?> Cache = new();

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public string CliId => "codex";

    public string? HistorySourcePath => _root;

    public bool IsSessionFile(string filePath) =>
        filePath.StartsWith(_root, StringComparison.OrdinalIgnoreCase) &&
        filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(_root, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxFiles);

        var rows = new List<TranscriptSummary>();
        foreach (var file in files)
        {
            if (Summarize(file) is { UserTurns: > 0 } summary)
            {
                rows.Add(summary);
            }
        }

        return rows;
    }

    private TranscriptSummary? Summarize(FileInfo info)
    {
        try
        {
            var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit is null || SameFolder(hit.GitBranch) ? hit : null;
            }

            string? id = null;
            string? cwd = null;
            string? first = null;
            var turns = 0;
            DateTimeOffset started = default;

            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var payload))
                    {
                        continue;
                    }

                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "session_meta")
                    {
                        id = payload.TryGetProperty("id", out var i) ? i.GetString() : null;
                        cwd = payload.TryGetProperty("cwd", out var c) ? c.GetString() : null;
                        if (payload.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var when))
                        {
                            started = when;
                        }

                        continue;
                    }

                    if (type == "response_item" && UserText(payload) is { Length: > 0 } text)
                    {
                        turns++;
                        first ??= text;
                    }
                }
                catch (JsonException)
                {
                }
            }

            // The folder rides along in GitBranch so the cached row can be re-checked against any project.
            var summary = id is null || turns == 0
                ? null
                : new TranscriptSummary
                {
                    SessionId = id,
                    FilePath = info.FullName,
                    StartedAt = started == default ? info.CreationTimeUtc : started,
                    UpdatedAt = info.LastWriteTimeUtc,
                    UserTurns = turns,
                    FirstPrompt = first,
                    GitBranch = cwd,
                };

            if (Cache.Count > 2000)
            {
                Cache.Clear();
            }

            Cache[key] = summary;
            return summary is not null && SameFolder(cwd) ? summary with { GitBranch = null } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool SameFolder(string? cwd) =>
        cwd is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(cwd.Replace('/', '\\')),
            Path.TrimEndingDirectorySeparator(projectPath.Replace('/', '\\')),
            StringComparison.OrdinalIgnoreCase);

    private static string? UserText(JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var pt) || pt.GetString() != "message" ||
            !payload.TryGetProperty("role", out var role) || role.GetString() != "user" ||
            !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var tx) && tx.GetString()?.Trim() is { Length: > 0 } text && text[0] != '<')
            {
                return TextClip.Truncate(text.ReplaceLineEndings(" "), 200);
            }
        }

        return null;
    }
}