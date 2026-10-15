using System.Text.Json;

using Codale.Core.Agents;
using Codale.Core.Projects;
using Codale.Core.Text;

namespace Codale.Agents.History;

/// <summary>
/// Lists the conversations Claude Code stored for a project (<c>~/.claude/projects/&lt;project&gt;/*.jsonl</c>).
/// The file name is the session id that <c>claude --resume</c> takes. Rows are for resuming, so
/// nothing is replayed here.
/// </summary>
public sealed class ClaudeSessionHistoryReader(string projectPath) : ICliHistoryReader
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), TranscriptSummary?> Cache = new();

    // A CLI run in the terminal keeps the default account (see CodaleAccount), not Codale's own,
    // so its transcripts live under ~/.claude (or wherever the user pointed CLAUDE_CONFIG_DIR).
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable(CodaleAccount.ClaudeVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects",
        ProjectPaths.ClaudeHistoryFolderName(projectPath));

    public string CliId => "claude";

    public string? HistorySourcePath => _directory;

    public bool IsSessionFile(string filePath) =>
        filePath.StartsWith(_directory, StringComparison.OrdinalIgnoreCase) &&
        filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var rows = new List<TranscriptSummary>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            if (Summarize(file) is { UserTurns: > 0 } summary)
            {
                rows.Add(summary);
            }
        }

        return [.. rows.OrderByDescending(s => s.UpdatedAt)];
    }

    private static TranscriptSummary? Summarize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = (path, info.Length, info.LastWriteTimeUtc);
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit;
            }

            string? title = null;
            string? first = null;
            string? branch = null;
            var turns = 0;
            DateTimeOffset started = default;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "custom-title" && root.TryGetProperty("customTitle", out var ct))
                    {
                        title = ct.GetString();
                    }

                    if (type != "user" || Flag(root, "isSidechain") || Flag(root, "isMeta"))
                    {
                        continue;
                    }

                    if (started == default && root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var when))
                    {
                        started = when;
                    }

                    branch ??= root.TryGetProperty("gitBranch", out var gb) ? gb.GetString() : null;

                    if (PromptText(root) is { Length: > 0 } text)
                    {
                        turns++;
                        first ??= text;
                    }
                }
                catch (JsonException)
                {
                }
            }

            var summary = turns == 0
                ? null
                : new TranscriptSummary
                {
                    SessionId = Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    StartedAt = started == default ? info.CreationTimeUtc : started,
                    UpdatedAt = info.LastWriteTimeUtc,
                    UserTurns = turns,
                    CustomTitle = title,
                    FirstPrompt = first,
                    GitBranch = branch,
                };

            if (Cache.Count > 2000)
            {
                Cache.Clear();
            }

            Cache[key] = summary;
            return summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Flag(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>What the user typed: not tool results, not the CLI's own bracketed notices.</summary>
    private static string? PromptText(JsonElement record)
    {
        if (!record.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        string? text = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" && block.TryGetProperty("text", out var tx))
                {
                    text = tx.GetString();
                    break;
                }
            }
        }

        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text[0] == '<' || text.StartsWith("Caveat:", StringComparison.Ordinal)
            ? null
            : TextClip.Truncate(text.ReplaceLineEndings(" "), 200);
    }
}