using System.Text.Json;

using Codale.Core.Agents;
using Codale.Core.Projects;
using Codale.Core.Text;

namespace Codale.Agents.Claude;

/// <summary>Summary of one stored conversation, for the session browser list.</summary>
public sealed record TranscriptSummary
{
    public required string SessionId { get; init; }
    public required string FilePath { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int UserTurns { get; init; }
    public string? CustomTitle { get; init; }
    public string? FirstPrompt { get; init; }
    public string? LastPrompt { get; init; }
    public string? GitBranch { get; init; }

    /// <summary>What the browser shows: the user's own title, else the opening prompt.</summary>
    public string Title
    {
        get
        {
            var text = CustomTitle ?? FirstPrompt ?? LastPrompt;

            if (string.IsNullOrWhiteSpace(text))
            {
                return "(empty session)";
            }

            var single = text.ReplaceLineEndings(" ").Trim();
            return single.Length > 90 ? TextClip.Truncate(single, 90) + "…" : single;
        }
    }
}

/// <summary>
/// Reads Claude Code's own transcript files so past conversations can be browsed and
/// replayed natively, rather than only being visible inside the CLI.
/// </summary>
/// <remarks>
/// The on-disk format overlaps the stream-json protocol but is not identical: records
/// carry extra fields (cwd, gitBranch, isSidechain, slug), there are bookkeeping types
/// the live stream never emits (custom-title, last-prompt, attachment, file-history-*),
/// and crucially a user message's <c>content</c> may be a plain string rather than an
/// array of blocks. All of that is handled here rather than in the live parser.
/// </remarks>
public sealed class ClaudeTranscriptReader
{
    private readonly string _historyDirectory;

    public ClaudeTranscriptReader(string projectPath, string? claudeHome = null) =>
        _historyDirectory = ProjectPaths.ClaudeHistoryDirectory(projectPath, claudeHome);

    public string HistoryDirectory => _historyDirectory;

    /// <summary>
    /// Summaries keyed by (path, length, last write). Opening the browser re-listed every
    /// transcript - full JSONL parses of multi-MB files - on every open; an unchanged
    /// file's summary is identical, so it is remembered instead. Cleared outright when
    /// it grows past any plausible session count.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), TranscriptSummary?> SummaryCache = new();

    /// <summary>Newest first. Sessions with no user turns are dropped as noise.</summary>
    public IReadOnlyList<TranscriptSummary> ListSessions()
    {
        if (!Directory.Exists(_historyDirectory))
        {
            return [];
        }

        var summaries = new List<TranscriptSummary>();

        foreach (var file in Directory.EnumerateFiles(_historyDirectory, "*.jsonl"))
        {
            if (Summarize(file) is { UserTurns: > 0 } summary)
            {
                summaries.Add(summary);
            }
        }

        return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public TranscriptSummary? Summarize(string filePath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (!info.Exists)
        {
            return null;
        }

        var key = (filePath, info.Length, info.LastWriteTimeUtc);
        if (SummaryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var summary = SummarizeUncached(filePath, info);
        if (SummaryCache.Count > 2000)
        {
            SummaryCache.Clear();
        }

        SummaryCache[key] = summary;
        return summary;
    }

    private TranscriptSummary? SummarizeUncached(string filePath, FileInfo info)
    {
        try
        {
            string? customTitle = null;
            string? lastPrompt = null;
            string? firstPrompt = null;
            string? branch = null;
            var userTurns = 0;
            DateTimeOffset started = default;
            DateTimeOffset updated = default;

            foreach (var line in ReadLinesShared(filePath))
            {
                if (Parse(line) is not { } record)
                {
                    continue;
                }

                var type = record.Str("type");

                if (record.Timestamp("timestamp") is { } ts)
                {
                    if (started == default)
                    {
                        started = ts;
                    }

                    updated = ts;
                }

                switch (type)
                {
                    case "custom-title":
                        customTitle = record.Str("customTitle");
                        break;

                    case "last-prompt":
                        lastPrompt = record.Str("lastPrompt") is { } last ? UserTurn.StripHostInstructions(last) : null;
                        break;

                    case "user":
                        // Sidechains are subagent conversations, not turns the user typed.
                        if (record.Bool("isSidechain"))
                        {
                            break;
                        }

                        branch ??= record.Str("gitBranch");

                        // The session list shows the user's words, not the host's ask-mode or fork preamble.
                        if (UserText(record) is { Length: > 0 } raw && UserTurn.StripHostInstructions(raw) is { Length: > 0 } text)
                        {
                            userTurns++;
                            if (!Codale.Core.Agents.SessionTitles.IsResetCommand(text))
                            {
                                firstPrompt ??= text;
                            }
                        }

                        break;
                }
            }

            return new TranscriptSummary
            {
                SessionId = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                StartedAt = started == default ? info.CreationTimeUtc : started,
                UpdatedAt = updated == default ? info.LastWriteTimeUtc : updated,
                UserTurns = userTurns,
                CustomTitle = customTitle,
                FirstPrompt = firstPrompt,
                LastPrompt = lastPrompt,
                GitBranch = branch,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Replays a stored conversation as events the chat panel already renders.</summary>
    public IEnumerable<AgentEvent> Replay(string filePath)
    {
        StreamReader reader;

        try
        {
            reader = OpenShared(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (reader.ReadLine() is { } line)
            {
                if (Parse(line) is not { } record)
                {
                    continue;
                }

                // Subagent chatter would swamp the main thread of the conversation.
                if (record.Bool("isSidechain"))
                {
                    continue;
                }

                var sessionId = record.Str("sessionId");
                var timestamp = record.Timestamp("timestamp");

                foreach (var e in Translate(record))
                {
                    yield return e with { SessionId = sessionId, Timestamp = timestamp };
                }
            }
        }
    }

    /// <summary>Opens a transcript the CLI may still be appending to (or rotating), without locking it out.</summary>
    private static StreamReader OpenShared(string filePath) =>
        new(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    private static IEnumerable<string> ReadLinesShared(string filePath)
    {
        using var reader = OpenShared(filePath);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IEnumerable<AgentEvent> Translate(JsonElement record)
    {
        switch (record.Str("type"))
        {
            case "user":
                if (UserText(record) is { Length: > 0 } text)
                {
                    yield return new UserMessageRecorded(UserTurn.StripHostInstructions(text));
                }

                foreach (var e in ToolResults(record))
                {
                    yield return e;
                }

                break;

            case "assistant":
                foreach (var e in Assistant(record))
                {
                    yield return e;
                }

                break;
        }
    }

    private static IEnumerable<AgentEvent> Assistant(JsonElement record)
    {
        if (record.Prop("message") is not { } message)
        {
            yield break;
        }

        var builder = new System.Text.StringBuilder();
        var hasThinking = false;

        foreach (var block in message.Items("content"))
        {
            switch (block.Str("type"))
            {
                case "text":
                    builder.Append(block.Str("text"));
                    break;

                case "thinking":
                    hasThinking = true;
                    break;

                case "tool_use":
                    yield return new ToolCallStarted
                    {
                        ToolUseId = block.Str("id") ?? "",
                        ToolName = block.Str("name") ?? "unknown",
                        Input = block.Prop("input") ?? default,
                    };
                    break;
            }
        }

        var body = builder.ToString();

        if (body.Length > 0 || hasThinking)
        {
            yield return new AssistantMessageCompleted
            {
                MessageId = message.Str("id") ?? "",
                Model = message.Str("model") ?? "unknown",
                Text = body,
                HasThinking = hasThinking,
            };
        }
    }

    private static IEnumerable<AgentEvent> ToolResults(JsonElement record)
    {
        if (record.Prop("message") is not { } message)
        {
            yield break;
        }

        // The stored record keeps the structured result beside the message, as the
        // live stream does - which is what gives replayed edits their diffs.
        var fileChange = ClaudeStreamParser.ParseFileChange(record.Prop("toolUseResult"));

        foreach (var block in message.Items("content"))
        {
            if (block.Str("type") != "tool_result")
            {
                continue;
            }

            yield return new ToolCallCompleted
            {
                ToolUseId = block.Str("tool_use_id") ?? "",
                IsError = block.Bool("is_error"),
                ResultText = null,
                FileChange = fileChange,
            };
        }
    }

    /// <summary>
    /// A stored user turn's content is either a bare string or an array of blocks, and
    /// tool results arrive as user records too - those are not something the user typed.
    /// </summary>
    private static string? UserText(JsonElement record)
    {
        // isMeta rows are CLI bookkeeping (the local-command caveat, skill bodies),
        // injected as user messages but never typed by the user.
        if (record.Bool("isMeta") ||
            record.Prop("message") is not { } message ||
            message.Prop("content") is not { } content)
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return LocalCommandText(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.Str("type") == "text" && block.Str("text") is { } text)
            {
                builder.Append(text);
            }
        }

        return builder.Length == 0 ? null : LocalCommandText(builder.ToString());
    }

    /// <summary>
    /// Local slash commands are stored as tagged pseudo-messages: the caveat and the
    /// command's stdout are dropped, and "&lt;command-name&gt;/x&lt;/command-name&gt;"
    /// plus its args reads back as the "/x args" the user typed.
    /// </summary>
    private static string? LocalCommandText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.TrimStart();

        if (trimmed.StartsWith("<local-command-caveat>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<local-command-stderr>", StringComparison.Ordinal))
        {
            return null;
        }

        if (Tag(trimmed, "command-name") is { } name)
        {
            var args = Tag(trimmed, "command-args");
            return string.IsNullOrWhiteSpace(args) ? name : $"{name} {args}";
        }

        return text;
    }

    private static string? Tag(string text, string tag)
    {
        var open = $"<{tag}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end].Trim();
    }

    private static JsonElement? Parse(string line)
    {
        // Every record carries a "type"; skip the full parse for anything that cannot be one.
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"type\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
