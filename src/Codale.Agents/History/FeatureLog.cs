using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Agents.History;

/// <summary>A place in a feature's log that survives archiving: which generation of the log, and the byte offset in it.</summary>
public readonly record struct LogPosition(int Generation, long Offset);

/// <summary>
/// One feature's running record: what the user asked and what the agent answered and did, from
/// every session and either CLI that worked on it. Append-only JSONL (<c>log.jsonl</c>), rotated
/// to <c>log.N.jsonl</c> once the summary has absorbed it. Nothing here is loaded into a chat on
/// open: the summary seeds new sessions, and the full log is read only on request, a tail at a time.
/// </summary>
/// <remarks>
/// Several chats can work on one feature at once, so every record carries the chat that wrote it
/// (<paramref name="chatId"/>) and each of its turns ends with a <c>turnEnd</c> record: a reader can
/// then tell the conversations apart, and finished work from work still under way. Appends and
/// archiving share one lock per log, so a summary rotating the log never loses a line written meanwhile.
/// </remarks>
public sealed class FeatureLog(string directory, string? chatId = null)
{
    private static readonly JsonSerializerOptions ChangeJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One gate per log folder, whichever chat or background job holds a <see cref="FeatureLog"/> on it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);

    private object Gate => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new object());

    private string LogPath => Path.Combine(directory, "log.jsonl");

    /// <summary>The chat this instance writes as; null for a reader.</summary>
    public string? ChatId => chatId;

    /// <summary>Bytes in the current log.</summary>
    public long Length => File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;

    /// <summary>How many times the log has been archived: an offset into it means something only within one generation.</summary>
    public int Generation => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "log.*.jsonl").Count() : 0;

    /// <summary>Where the log ends now, as a position <see cref="ReadSince(LogPosition, string?)"/> can resume from.</summary>
    public LogPosition Position
    {
        get
        {
            lock (Gate)
            {
                return new LogPosition(Generation, Length);
            }
        }
    }

    /// <summary>
    /// The records after <paramref name="position"/>. When the log was archived since, the archive holding that
    /// position is read from it onward, then the logs after it - nothing written meanwhile is skipped.
    /// </summary>
    public (IReadOnlyList<HandoffEntry> Entries, int Turns) ReadSince(LogPosition position, string? excludeChat = null)
    {
        var generation = Generation;
        if (position.Generation >= generation)
        {
            return ReadSince(position.Offset, excludeChat);
        }

        var entries = new List<HandoffEntry>();
        var turns = 0;
        for (var g = position.Generation + 1; g <= generation; g++)
        {
            var (part, partTurns) = Read(Path.Combine(directory, $"log.{g}.jsonl"), g == position.Generation + 1 ? position.Offset : 0, excludeChat);
            entries.AddRange(part);
            turns += partTurns;
        }

        var (current, currentTurns) = ReadSince(0, excludeChat);
        entries.AddRange(current);
        return (entries, turns + currentTurns);
    }

    /// <summary>Notes that a session of <paramref name="cli"/> took up the feature.</summary>
    public void Attach(string cli) => Append(new { type = "session", cli, at = DateTimeOffset.Now });

    public void AppendUser(string text) => Append(new { type = "user", text, at = DateTimeOffset.Now });

    /// <summary>Notes that the chat's turn is over, and how it ended ("success", an error subtype, "stopped").</summary>
    public void EndTurn(string outcome) => Append(new { type = "turnEnd", outcome, at = DateTimeOffset.Now });

    /// <summary>Notes the main conversation's replies; tool calls, subagents and stream plumbing are not history.</summary>
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
                Append(new { type = "toolDone", id = done.ToolUseId, error = done.IsError, change = done.FileChange, changes = done.FileChanges, at = DateTimeOffset.Now });
                break;
        }
    }

    /// <summary>
    /// The conversation after <paramref name="offset"/> bytes of the log, as the summary writer reads it, and how
    /// many user turns it holds. Each entry names the chat that wrote it; <paramref name="excludeChat"/> leaves
    /// one chat's own records out (a chat catching up on the others).
    /// </summary>
    public (IReadOnlyList<HandoffEntry> Entries, int Turns) ReadSince(long offset, string? excludeChat = null) =>
        Read(LogPath, offset, excludeChat);

    private static (IReadOnlyList<HandoffEntry> Entries, int Turns) Read(string path, long offset, string? excludeChat)
    {
        var entries = new List<HandoffEntry>();
        var turns = 0;

        foreach (var record in ReadRecords(path, offset))
        {
            var chat = record.Str("chat");
            if (excludeChat is not null && chat == excludeChat)
            {
                continue;
            }

            switch (record.Str("type"))
            {
                case "user" when record.Str("text") is { Length: > 0 } user:
                    turns++;
                    entries.Add(new HandoffEntry(HandoffRole.User, user, chat));
                    break;

                case "assistant" when record.Str("text") is { Length: > 0 } reply:
                    entries.Add(new HandoffEntry(HandoffRole.Assistant, reply, chat));
                    break;

                case "tool":
                    entries.Add(new HandoffEntry(HandoffRole.Tool, DescribeTool(record), chat));
                    break;

                case "turnEnd":
                    entries.Add(new HandoffEntry(HandoffRole.TurnEnd, record.Str("outcome") ?? "", chat));
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

    /// <summary>Notes that the plan proposed by <paramref name="toolUseId"/> was decided ("accepted" or "revised").</summary>
    public void EndPlan(string toolUseId, string outcome) =>
        Append(new { type = "planEnd", id = toolUseId, outcome, at = DateTimeOffset.Now });

    /// <summary>The newest proposed plan nobody accepted or sent back: stopped on, or set aside for another request.</summary>
    public ToolCallStarted? LatestOpenPlan()
    {
        var decided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files().Reverse())
        {
            foreach (var record in ReadRecords(file, 0).Reverse())
            {
                switch (record.Str("type"))
                {
                    case "planEnd" when record.Str("id") is { } ended:
                        decided.Add(ended);
                        break;

                    case "tool" when record.Str("name") == "ExitPlanMode" &&
                                     !decided.Contains(record.Str("id") ?? "") &&
                                     Translate(record) is ToolCallStarted plan:
                        return plan with { Timestamp = record.Timestamp("at") };
                }
            }
        }

        return null;
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
    public void Archive() => ArchiveUpTo(long.MaxValue);

    /// <summary>
    /// Archives the first <paramref name="offset"/> bytes - what a summary has absorbed - and starts the
    /// new log with whatever was appended past them (another chat writing while the summary was made),
    /// so no record ever lands in an archive without having been summarised.
    /// </summary>
    public void ArchiveUpTo(long offset)
    {
        lock (Gate)
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            byte[] tail;
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var start = Math.Min(offset, stream.Length);
                tail = new byte[stream.Length - start];
                stream.Seek(start, SeekOrigin.Begin);
                stream.ReadExactly(tail);
            }

            var next = Directory.EnumerateFiles(directory, "log.*.jsonl")
                .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Split('.').ElementAtOrDefault(1), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var archive = Path.Combine(directory, $"log.{next}.jsonl");
            File.Move(LogPath, archive);
            if (tail.Length > 0)
            {
                File.WriteAllBytes(LogPath, tail);

                // The archive keeps only what the summary read: the carried lines live in the new log alone.
                using var trim = new FileStream(archive, FileMode.Open, FileAccess.Write, FileShare.Read);
                trim.SetLength(trim.Length - tail.Length);
            }

            _tools.Clear();
        }
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
        var line = JsonSerializer.SerializeToNode(record) is System.Text.Json.Nodes.JsonObject node
            ? WithChat(node)
            : JsonSerializer.Serialize(record);

        // A summary rotating the log, or another window's append, holds the file for a moment:
        // one retry rides that out. Past it, history is a convenience - a full disk or a locked
        // file must never take the chat down.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    private string WithChat(System.Text.Json.Nodes.JsonObject node)
    {
        if (chatId is not null)
        {
            node["chat"] = chatId;
        }

        return node.ToJsonString();
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
            FileChanges = record.Prop("changes") is { ValueKind: JsonValueKind.Array } changes
                ? changes.Deserialize<List<FileChange>>(ChangeJson)
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