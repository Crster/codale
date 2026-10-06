using System.Text.Json;

using Codale.Core.Agents;
using Codale.Core.Text;

namespace Codale.Agents.Claude;

/// <summary>
/// Maps one line of <c>claude --output-format stream-json</c> onto zero or more
/// <see cref="AgentEvent"/>s.
/// </summary>
/// <remarks>
/// Deliberately pure and stateless: it takes a string and returns events, so the
/// whole protocol surface can be tested by replaying recorded transcripts with no
/// child process, no network and no tokens. See <c>tests/fixtures/claude/</c>.
/// </remarks>
public sealed class ClaudeStreamParser
{
    /// <summary>
    /// System subtypes that are real protocol members but carry nothing the UI needs.
    /// Listed explicitly so anything genuinely new still surfaces as <see cref="UnknownEvent"/>.
    /// The task_* family is the CLI's background-task lifecycle (spawned shells, queued
    /// jobs) - chatter, not transcript material; file changes arrive as tool results.
    /// </summary>
    private static readonly HashSet<string> IgnoredSystemSubtypes =
    [
        "hook_started",
        "thinking_tokens",
        "background_tasks_changed",
        "task_updated",
        "task_progress",

        // Lifecycle pings: hook streaming, session-state transitions and file
        // persistence bookkeeping. None of them changes what the transcript shows.
        "hook_progress",
        "session_state_changed",
        "files_persisted",
        "post_turn_summary",
    ];

    /// <summary>
    /// Message types that are real protocol members but carry nothing the UI needs.
    /// <c>tool_progress</c> streams a running tool's intermediate state; the tool row
    /// already shows a running state, and the finished result arrives on its own.
    /// </summary>
    private static readonly HashSet<string> IgnoredMessageTypes = ["tool_progress"];

    public IEnumerable<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // The CLI also writes non-JSON noise on occasion; never let it kill the session.
            return [new UnknownEvent("unparseable", Truncate(line))];
        }

        using (doc)
        {
            // Events outlive the document, so the only element they keep (a tool's input) is cloned out of it.
            return ParseRoot(doc.RootElement, line).ToList();
        }
    }

    private IEnumerable<AgentEvent> ParseRoot(JsonElement root, string raw)
    {
        var type = root.Str("type");
        var sessionId = root.Str("session_id");
        var uuid = root.Str("uuid");
        var timestamp = root.Timestamp("timestamp");

        IEnumerable<AgentEvent> events = type switch
        {
            "system" => ParseSystem(root, raw),
            "assistant" => ParseAssistant(root),
            "user" => ParseUser(root),
            "stream_event" => ParseStreamEvent(root),
            "result" => [ParseResult(root)],
            "rate_limit_event" => [ParseRateLimit(root)],
            "control_request" => ParseControlRequest(root),

            // Tolerated at either level: some CLI builds wrap it in "system", some
            // send the bare type.
            "conversation_reset" => [new ConversationReset()],

            "prompt_suggestion" => root.Str("suggestion") is { Length: > 0 } suggestion ? [new PromptSuggested(suggestion.Trim())] : [],

            // Answers to our own control requests are correlated by the driver, not the UI.
            "control_response" => [],

            _ when type is not null && IgnoredMessageTypes.Contains(type) => [new CliMessageIgnored(type, Truncate(raw))],

            _ => [new UnknownEvent(type ?? "missing-type", Truncate(raw))],
        };

        foreach (var e in events)
        {
            yield return e with { SessionId = sessionId, Uuid = uuid, Timestamp = timestamp };
        }
    }

    private static IEnumerable<AgentEvent> ParseSystem(JsonElement root, string raw)
    {
        var subtype = root.Str("subtype");

        switch (subtype)
        {
            case "init":
                yield return new SessionInitialized
                {
                    Model = root.Str("model") ?? "unknown",
                    WorkingDirectory = root.Str("cwd") ?? "",
                    PermissionMode = root.Str("permissionMode") ?? "default",
                    Tools = root.StrArray("tools"),
                    SlashCommands = root.StrArray("slash_commands"),
                    Agents = root.StrArray("agents"),
                    Skills = root.StrArray("skills"),
                    Version = root.Str("claude_code_version"),
                };
                break;

            case "permission_denied":
                yield return new PermissionDenied
                {
                    ToolName = root.Str("tool_name") ?? "unknown",
                    ToolUseId = root.Str("tool_use_id"),
                    Message = root.Str("message"),
                };
                break;

            case "conversation_reset":
                yield return new ConversationReset();
                break;

            case "compact_boundary":
                var meta = root.Prop("compact_metadata");
                yield return new ConversationCompacted(
                    meta?.Str("trigger"),
                    meta?.Int("pre_tokens"));
                break;

            case "hook_response":
                var outcome = root.Str("outcome");
                var exitCode = root.Int("exit_code");
                if ((exitCode is { } code && code != 0) || (outcome is not null && outcome != "success"))
                {
                    var stderr = root.Str("stderr");
                    yield return new HookFailed(
                        root.Str("hook_name") ?? "hook",
                        root.Str("hook_event"),
                        exitCode,
                        outcome,
                        string.IsNullOrWhiteSpace(stderr) ? root.Str("output") : stderr);
                }
                else
                {
                    yield return new CliMessageIgnored("system/hook_response", Truncate(raw));
                }

                break;

            case "status":
                if (root.Str("status") is { Length: > 0 } status)
                {
                    yield return new CliStatusChanged(status);
                }

                break;

            case "informational":
                if ((root.Str("message") ?? root.Str("text") ?? root.Str("content")) is { Length: > 0 } info)
                {
                    yield return new CliInformation(info.Trim());
                }
                else
                {
                    yield return new CliMessageIgnored("system/informational", Truncate(raw));
                }

                break;

            case "api_retry":
                // The CLI is backing off after a failed API call; say so rather than let
                // the turn look hung. Fields are optional across CLI builds.
                var attempt = root.Int("attempt");
                var maxRetries = root.Int("max_retries");
                var delayMs = root.Int("retry_delay_ms");
                var errorStatus = root.Int("error_status");
                var retry = attempt is { } n
                    ? maxRetries is { } max ? $"retry {n} of {max}" : $"retry {n}"
                    : "retrying";
                var cause = errorStatus is { } statusCode ? $"API error {statusCode}" : root.Str("error") ?? "API error";
                var wait = delayMs is { } ms and > 0 ? $" in {Math.Max(1, (int)Math.Round(ms / 1000.0))}s" : "";
                yield return new CliInformation($"{cause}: {retry}{wait}.") { IsWarning = true };
                break;

            case "commands_changed":
                var names = (root.Prop("commands") ?? root.Prop("slash_commands")) is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray()
                        .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.Str("name"))
                        .OfType<string>()
                        .ToList()
                    : [];

                yield return names.Count > 0
                    ? new CommandsChanged(names)
                    : new CliMessageIgnored("system/commands_changed", Truncate(raw));
                break;

            case "vcs_state_changed":
                yield return new VcsStateChanged();
                break;

            case "task_started" or "task_notification":
                if (root.Str("task_id") is { Length: > 0 } taskId)
                {
                    yield return new BackgroundTaskChanged(
                        taskId,
                        root.Str("tool_use_id"),
                        subtype == "task_started" ? null : root.Str("status") ?? "completed",
                        subtype == "task_started" ? root.Str("prompt") : null);
                }

                break;

            default:
                yield return subtype is not null && IgnoredSystemSubtypes.Contains(subtype)
                    ? new CliMessageIgnored($"system/{subtype}", Truncate(raw))
                    : new UnknownEvent($"system/{subtype ?? "?"}", Truncate(raw));

                break;
        }
    }

    private static IEnumerable<AgentEvent> ParseAssistant(JsonElement root)
    {
        if (root.Prop("message") is not { } message)
        {
            yield break;
        }

        var parentToolUseId = root.Str("parent_tool_use_id");
        var model = message.Str("model") ?? "unknown";
        var messageId = message.Str("id") ?? "";

        var text = new System.Text.StringBuilder();
        var hasThinking = false;
        var thinking = new System.Text.StringBuilder();

        foreach (var block in message.Items("content"))
        {
            switch (block.Str("type"))
            {
                case "text":
                    text.Append(block.Str("text"));
                    break;

                case "thinking":
                    hasThinking = true;
                    thinking.Append(block.Str("thinking"));
                    break;

                case "tool_use":
                    yield return new ToolCallStarted
                    {
                        ToolUseId = block.Str("id") ?? "",
                        ToolName = block.Str("name") ?? "unknown",
                        Input = CloneInput(block.Prop("input")),
                        ParentToolUseId = parentToolUseId,
                    };
                    break;
            }
        }

        yield return new AssistantMessageCompleted
        {
            MessageId = messageId,
            Model = model,
            Text = text.ToString(),
            HasThinking = hasThinking,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            Usage = ParseUsage(message.Prop("usage")),
            ParentToolUseId = parentToolUseId,
        };
    }

    private static IEnumerable<AgentEvent> ParseUser(JsonElement root)
    {
        if (root.Prop("message") is not { } message)
        {
            yield break;
        }

        // The structured result sits beside the message, not inside the content block.
        var fileChange = ParseFileChange(root.Prop("tool_use_result"));

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
                ResultText = ContentAsText(block.Prop("content")),
                FileChange = fileChange,
                Images = ImagesOf(block.Prop("content")),
            };
        }
    }

    private static IEnumerable<AgentEvent> ParseStreamEvent(JsonElement root)
    {
        if (root.Prop("event") is not { } ev || ev.Str("type") != "content_block_delta")
        {
            yield break;
        }

        if (ev.Prop("delta") is not { } delta)
        {
            yield break;
        }

        var parent = root.Str("parent_tool_use_id");

        switch (delta.Str("type"))
        {
            case "text_delta" when delta.Str("text") is { Length: > 0 } t:
                yield return new AssistantTextDelta(t) { ParentToolUseId = parent };
                break;

            case "thinking_delta" when delta.Str("thinking") is { Length: > 0 } t:
                yield return new AssistantThinkingDelta(t) { ParentToolUseId = parent };
                break;

            // signature_delta and input_json_delta carry no user-visible text.
        }
    }

    private static AgentEvent ParseResult(JsonElement root)
    {
        var usage = ParseUsage(root.Prop("usage"));
        int? contextWindow = null;

        // modelUsage is keyed by model id; the turn's context window lives there.
        if (root.Prop("modelUsage") is { ValueKind: JsonValueKind.Object } modelUsage)
        {
            foreach (var entry in modelUsage.EnumerateObject())
            {
                contextWindow = entry.Value.Int("contextWindow") ?? contextWindow;
            }
        }

        return new TurnCompleted
        {
            Subtype = root.Str("subtype") ?? "unknown",
            TotalCostUsd = root.Dec("total_cost_usd"),
            ApiDuration = root.Int("duration_api_ms") is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            StopReason = root.Str("stop_reason"),
            Usage = usage,
            ContextWindow = contextWindow,
        };
    }

    private static AgentEvent ParseRateLimit(JsonElement root)
    {
        var info = root.Prop("rate_limit_info") ?? default;
        var windows = info.Prop("unifiedWindows");
        var fiveHour = windows?.Prop("five_hour");
        var sevenDay = windows?.Prop("seven_day");

        return new RateLimitUpdated(new RateLimitSnapshot
        {
            Status = info.Str("status"),
            LimitType = info.Str("rateLimitType"),
            IsUsingOverage = info.Bool("isUsingOverage"),
            FiveHourUtilization = fiveHour?.Dbl("utilization"),
            FiveHourResetsAt = fiveHour?.EpochSeconds("resetsAt"),
            SevenDayUtilization = sevenDay?.Dbl("utilization"),
            SevenDayResetsAt = sevenDay?.EpochSeconds("resetsAt"),
        });
    }

    private static IEnumerable<AgentEvent> ParseControlRequest(JsonElement root)
    {
        if (root.Prop("request") is not { } request || request.Str("subtype") != "can_use_tool")
        {
            yield break;
        }

        var suggestions = new List<PermissionSuggestion>();
        foreach (var s in request.Items("permission_suggestions"))
        {
            if (s.Str("type") is { } t)
            {
                suggestions.Add(new PermissionSuggestion
                {
                    Type = t,
                    Mode = s.Str("mode"),
                    Destination = s.Str("destination"),
                });
            }
        }

        yield return new ApprovalRequested
        {
            RequestId = root.Str("request_id") ?? "",
            ToolName = request.Str("tool_name") ?? "unknown",
            DisplayName = request.Str("display_name"),
            ToolUseId = request.Str("tool_use_id"),
            Input = CloneInput(request.Prop("input")),
            Suggestions = suggestions,
        };
    }

    private static UsageSnapshot? ParseUsage(JsonElement? usage)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } u)
        {
            return null;
        }

        return new UsageSnapshot
        {
            InputTokens = u.Int("input_tokens") ?? 0,
            OutputTokens = u.Int("output_tokens") ?? 0,
            CacheReadInputTokens = u.Int("cache_read_input_tokens") ?? 0,
            CacheCreationInputTokens = u.Int("cache_creation_input_tokens") ?? 0,
            ThinkingTokens = u.Prop("output_tokens_details")?.Int("thinking_tokens") ?? 0,
        };
    }

    /// <summary>
    /// Reads a file tool's structured result. Write results say what they did in
    /// "type"; Edit and MultiEdit results carry no type at all - only the strings they
    /// swapped and the patch - so an edit-shaped result without one is an update.
    /// Shared with the transcript reader, whose stored "toolUseResult" is the same shape.
    /// </summary>
    internal static FileChange? ParseFileChange(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } r || r.Str("filePath") is not { } path)
        {
            return null;
        }

        var looksLikeEdit = r.Prop("oldString") is not null ||
                            r.Prop("edits") is not null ||
                            r.Prop("structuredPatch") is { ValueKind: JsonValueKind.Array };

        var kind = r.Str("type") switch
        {
            "create" => FileChangeKind.Create,
            "update" => FileChangeKind.Update,
            "delete" => FileChangeKind.Delete,
            _ when looksLikeEdit => FileChangeKind.Update,
            _ => FileChangeKind.Unknown,
        };

        return new FileChange
        {
            Kind = kind,
            FilePath = path,
            NewContent = r.Str("content"),
            OriginalContent = r.Str("originalFile") ?? r.Str("originalFileContents"),
            UserModified = r.Bool("userModified"),
            Patch = ParsePatch(r),
        };
    }

    /// <summary>
    /// <c>structuredPatch</c>: [{oldStart, oldLines, newStart, newLines, lines: ["-a", "+b", " c"]}].
    /// Null when absent or empty - a create has "[]", which says nothing a diff of the
    /// content would not.
    /// </summary>
    private static IReadOnlyList<FilePatchHunk>? ParsePatch(JsonElement result)
    {
        var hunks = new List<FilePatchHunk>();

        foreach (var hunk in result.Items("structuredPatch"))
        {
            var lines = hunk.StrArray("lines");
            if (lines.Count == 0)
            {
                continue;
            }

            hunks.Add(new FilePatchHunk
            {
                OldStart = hunk.Int("oldStart") ?? 1,
                NewStart = hunk.Int("newStart") ?? 1,
                Lines = lines,
            });
        }

        return hunks.Count > 0 ? hunks : null;
    }

    private static IReadOnlyList<ResultImage>? ImagesOf(JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Array } blocks)
        {
            return null;
        }

        var images = new List<ResultImage>();
        foreach (var b in blocks.EnumerateArray())
        {
            if (b.Str("type") == "image" && b.Prop("source") is { } src && src.Str("data") is { Length: > 0 } data)
            {
                images.Add(new ResultImage(src.Str("media_type") ?? "image/png", data));
            }
        }

        return images.Count > 0 ? images : null;
    }

    /// <summary>tool_result content is either a bare string or an array of content blocks.</summary>
    private static string? ContentAsText(JsonElement? content) => content switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Array } a => string.Concat(
            a.EnumerateArray()
             .Where(b => b.Str("type") == "text")
             .Select(b => b.Str("text"))),
        _ => null,
    };

    private static JsonElement CloneInput(JsonElement? input) => input is { } element ? element.Clone() : default;

    private static string Truncate(string s, int max = 2000) => TextClip.Truncate(s, max);
}
