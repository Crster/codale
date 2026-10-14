using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Agents.History;

/// <summary>
/// One feature's running record: what the user asked and what the agent answered and did, from
/// every session and either CLI that worked on it. Append-only JSONL (<c>log.jsonl</c>), rotated
/// to <c>log.N.jsonl</c> once the summary has absorbed it. Nothing here is loaded into a chat on
/// open: the summary seeds new sessions, and the full log is read only on request, a tail at a time.
/// </summary>
public sealed class FeatureLog(string directory)
{
    private static readonly JsonSerializerOptions ChangeJson = new() { PropertyNameCaseInsensitive = true };

    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);

    private string LogPath => Path.Combine(directory, "log.jsonl");

    /// <summary>Bytes in the current log.</summary>
    public long Length => File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;

    /// <summary>Notes that a session of <paramref name="cli"/> took up the feature.</summary>
    public void Attach(string cli) => Append(new { type = "session", cli, at = DateTimeOffset.Now });

    public void AppendUser(string text) => Append(new { type = "user", text, at = DateTimeOffset.Now });

    /// <summary>Notes what the main conversation did; subagent chatter and stream plumbing are not history.</summary>
    public void Append(AgentEvent e)
    {
        switch (e)
        {
            case AssistantMessageCompleted { ParentToolUseId: null, Text.Length: > 0 } reply:
                Append(new { type = "assistant", id = reply.MessageId, model = reply.Model, text = reply.Text, at = DateTimeOffset.Now });
                break;

            case ToolCallStarted { ParentToolUseId: null } started:
                _tools.Add(started.ToolUseId);
                Append(new
                {
                    type = "tool",
                    id = started.ToolUseId,
                    name = started.ToolName,
                    input = started.Input.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : started.Input,
                    at = DateTimeOffset.Now,
                });
                break;

            case ToolCallCompleted done when _tools.Contains(done.ToolUseId):
                Append(new { type = "toolDone", id = done.ToolUseId, error = done.IsError, change = done.FileChange, at = DateTimeOffset.Now });
                break;
        }
    }

    /// <summary>The conversation after <paramref name="offset"/> bytes of the log, as the summary writer reads it, and how many user turns it holds.</summary>
    public (IReadOnlyList<HandoffEntry> Entries, int Turns) ReadSince(long offset)
    {
        var entries = new List<HandoffEntry>();
        var turns = 0;

        foreach (var record in ReadRecords(LogPath, offset))
        {
            switch (record.Str("type"))
            {
                case "user" when record.Str("text") is { Length: > 0 } user:
                    turns++;
                    entries.Add(new HandoffEntry(HandoffRole.User, user));
                    break;

                case "assistant" when record.Str("text") is { Length: > 0 } reply:
                    entries.Add(new HandoffEntry(HandoffRole.Assistant, reply));
                    break;

                case "tool":
                    entries.Add(new HandoffEntry(HandoffRole.Tool, DescribeTool(record)));
                    break;
            }
        }

        return (entries, turns);
    }

    /// <summary>
    /// The last <paramref name="maxEvents"/> events of the feature, oldest first, reaching back
    /// into archived logs when the current one holds fewer; <paramref name="hasMore"/> says earlier ones exist.
    /// </summary>
    public IReadOnlyList<AgentEvent> ReadTail(int maxEvents, out bool hasMore)
    {
        var events = new List<AgentEvent>();
        hasMore = false;

        // Newest file first, so a long history costs only the pages asked for.
        foreach (var file in Files().Reverse())
        {
            var fromFile = ReadRecords(file, 0)
                .Select(r => Translate(r) is { } e ? e with { Timestamp = r.Timestamp("at") } : null)
                .OfType<AgentEvent>()
                .ToList();

            events.InsertRange(0, fromFile);
            if (events.Count > maxEvents)
            {
                break;
            }
        }

        if (events.Count > maxEvents)
        {
            hasMore = true;
            events.RemoveRange(0, events.Count - maxEvents);
        }
        else
        {
            hasMore = Files().Count() > 1 && events.Count == maxEvents;
        }

        return events;
    }

    /// <summary>The newest task-list tool call in the feature, for restoring the session panel.</summary>
    public ToolCallStarted? LatestTodoUpdate()
    {
        foreach (var file in Files().Reverse())
        {
            var records = ReadRecords(file, 0).ToList();
            for (var i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].Str("type") == "tool" &&
                    TodoParser.IsTodosTool(records[i].Str("name") ?? "") &&
                    Translate(records[i]) is ToolCallStarted started)
                {
                    return started;
                }
            }
        }

        return null;
    }

    /// <summary>Moves the current log to the next archive and starts an empty one.</summary>
    public void Archive()
    {
        if (!File.Exists(LogPath))
        {
            return;
        }

        var next = Directory.EnumerateFiles(directory, "log.*.jsonl")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Split('.').ElementAtOrDefault(1), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        File.Move(LogPath, Path.Combine(directory, $"log.{next}.jsonl"));
        _tools.Clear();
    }

    /// <summary>Removes the feature's whole log folder.</summary>
    public void Delete()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Archives oldest first, then the current log.</summary>
    private IEnumerable<string> Files()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var archives = Directory.EnumerateFiles(directory, "log.*.jsonl")
            .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Split('.').ElementAtOrDefault(1), out var n) ? n : 0);
        return File.Exists(LogPath) ? archives.Append(LogPath) : archives;
    }

    private void Append<T>(T record)
    {
        // History is a convenience: a full disk or a locked file must never take the chat down.
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string DescribeTool(JsonElement record)
    {
        var name = record.Str("name") ?? "tool";
        if (record.Prop("input") is { ValueKind: JsonValueKind.Object } input)
        {
            foreach (var key in new[] { "file_path", "path", "command", "pattern", "description", "query" })
            {
                if (input.Str(key) is { Length: > 0 } value)
                {
                    return $"{name}({(value.Length > 120 ? value[..120] : value)})";
                }
            }
        }

        return name;
    }

    private static AgentEvent? Translate(JsonElement record) => record.Str("type") switch
    {
        "user" when record.Str("text") is { Length: > 0 } text => new UserMessageRecorded(text),
        "assistant" => new AssistantMessageCompleted
        {
            MessageId = record.Str("id") ?? "",
            Model = record.Str("model") ?? "unknown",
            Text = record.Str("text") ?? "",
        },
        "tool" => new ToolCallStarted
        {
            ToolUseId = record.Str("id") ?? "",
            ToolName = record.Str("name") ?? "unknown",
            Input = record.Prop("input") ?? default,
        },
        "toolDone" => new ToolCallCompleted
        {
            ToolUseId = record.Str("id") ?? "",
            IsError = record.Bool("error"),
            FileChange = record.Prop("change") is { ValueKind: JsonValueKind.Object } change
                ? change.Deserialize<FileChange>(ChangeJson)
                : null,
        },
        _ => null,
    };

    /// <summary>The records of a file from a byte offset (which is always a line start); unreadable lines are skipped.</summary>
    private static IEnumerable<JsonElement> ReadRecords(string path, long offset)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (stream)
        {
            if (offset > 0)
            {
                stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement record;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    record = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                yield return record;
            }
        }
    }
}
