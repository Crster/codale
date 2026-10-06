using System.Text.Json;

namespace Codale.Core.Agents;

/// <summary>
/// Normalised view of anything the Claude CLI can tell us. The driver maps its
/// stream-json wire format onto this set, so panels bind to <see cref="AgentEvent"/>
/// and never to the CLI's JSON.
/// </summary>
public abstract record AgentEvent
{
    public string? SessionId { get; init; }
    public string? Uuid { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>Session is up. Carries everything the UI needs to configure itself.</summary>
public sealed record SessionInitialized : AgentEvent
{
    public required string Model { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string PermissionMode { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> SlashCommands { get; init; } = [];
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<string> Skills { get; init; } = [];
    public string? Version { get; init; }
}

/// <summary>
/// A user turn read back from a stored transcript. The live stream never needs this
/// (the UI already knows what it sent), but replaying history does.
/// </summary>
public sealed record UserMessageRecorded(string Text) : AgentEvent;

public sealed record AssistantTextDelta(string Text) : AgentEvent
{
    /// <summary>Set when a subagent, not the main loop, is the one writing.</summary>
    public string? ParentToolUseId { get; init; }
}

public sealed record AssistantThinkingDelta(string Text) : AgentEvent
{
    public string? ParentToolUseId { get; init; }
}

/// <summary>A complete assistant message landed (authoritative; deltas are a preview of it).</summary>
public sealed record AssistantMessageCompleted : AgentEvent
{
    public required string MessageId { get; init; }
    public required string Model { get; init; }
    public string Text { get; init; } = "";
    public bool HasThinking { get; init; }

    /// <summary>The thinking text, when the provider returns it; a subagent's log shows it.</summary>
    public string? Thinking { get; init; }

    public UsageSnapshot? Usage { get; init; }

    /// <summary>Set when this message came from a subagent rather than the main loop.</summary>
    public string? ParentToolUseId { get; init; }
}

public sealed record ToolCallStarted : AgentEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public JsonElement Input { get; init; }
    public string? ParentToolUseId { get; init; }
}

public sealed record ToolCallCompleted : AgentEvent
{
    public required string ToolUseId { get; init; }
    public bool IsError { get; init; }
    public string? ResultText { get; init; }

    /// <summary>Present when the tool touched a file — drives the session changes panel.</summary>
    public FileChange? FileChange { get; init; }

    /// <summary>Images the tool returned (a browser screenshot); the session panel keeps them as artifacts.</summary>
    public IReadOnlyList<ResultImage>? Images { get; init; }
}

/// <summary>An image in a tool result: its media type and base64 data.</summary>
public sealed record ResultImage(string MediaType, string Base64);

/// <summary>The CLI is asking the host whether a tool may run. Blocks the turn until answered.</summary>
public sealed record ApprovalRequested : AgentEvent
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public string? DisplayName { get; init; }
    public string? ToolUseId { get; init; }
    public JsonElement Input { get; init; }

    /// <summary>CLI-proposed shortcuts, e.g. "switch this session to acceptEdits".</summary>
    public IReadOnlyList<PermissionSuggestion> Suggestions { get; init; } = [];
}

/// <summary>A tool was refused without the host being consulted (no host approval route wired up).</summary>
public sealed record PermissionDenied : AgentEvent
{
    public required string ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public string? Message { get; init; }
}

/// <summary>The CLI's guess at what the user will type next, emitted after a turn when prompt suggestions are on.</summary>
public sealed record PromptSuggested(string Text) : AgentEvent;

/// <summary>
/// A background task (a subagent or shell the CLI runs beyond the turn) started or changed state.
/// Status is null when it just started; otherwise completed, failed or stopped. TaskId is what
/// stopping it goes by.
/// </summary>
public sealed record BackgroundTaskChanged(string TaskId, string? ToolUseId, string? Status, string? Prompt = null) : AgentEvent;

/// <summary>The repository changed under the session (a branch switch, a commit, a shell command): git views are stale.</summary>
public sealed record VcsStateChanged : AgentEvent;

/// <summary>
/// A CLI message the transcript has no use for, kept so it can be logged: building on
/// real payloads beats guessing at an undocumented protocol.
/// </summary>
public sealed record CliMessageIgnored(string Kind, string Raw) : AgentEvent;

/// <summary>A user hook ran and did not succeed (non-zero exit or a non-success outcome).</summary>
public sealed record HookFailed(string HookName, string? HookEvent, int? ExitCode, string? Outcome, string? Detail) : AgentEvent;

/// <summary>The CLI's own phase changed, e.g. requesting (waiting on the API) or compacting.</summary>
public sealed record CliStatusChanged(string Status) : AgentEvent;

/// <summary>Human-readable text from the CLI that is not part of the conversation.</summary>
public sealed record CliInformation(string Text) : AgentEvent
{
    public bool IsWarning { get; init; }
}

/// <summary>The set of slash commands changed (skills and commands finished loading or were edited).</summary>
public sealed record CommandsChanged(IReadOnlyList<string> Commands) : AgentEvent;

/// <summary>The CLI cleared the conversation (/clear): the transcript starts over.</summary>
public sealed record ConversationReset : AgentEvent;

/// <summary>The CLI compacted the conversation (manually or because the context filled).</summary>
public sealed record ConversationCompacted(string? Trigger, long? PreTokens) : AgentEvent;

/// <summary>A provider-level failure that is not tied to a particular tool call.</summary>
public sealed record AgentError(string Message) : AgentEvent;

public sealed record UsageUpdated(UsageSnapshot Usage) : AgentEvent;

public sealed record RateLimitUpdated(RateLimitSnapshot Limits) : AgentEvent;

/// <summary>Running input + output token total for the session, for providers with no quota to meter (BYOK).</summary>
public sealed record SessionTokensUpdated(long Total) : AgentEvent;

public sealed record TurnCompleted : AgentEvent
{
    public required string Subtype { get; init; }

    public bool IsError => !string.Equals(Subtype, "success", StringComparison.Ordinal);

    public decimal? TotalCostUsd { get; init; }
    public TimeSpan? ApiDuration { get; init; }
    public string? StopReason { get; init; }
    public UsageSnapshot? Usage { get; init; }

    /// <summary>Authoritative context window for the model that served this turn.</summary>
    public int? ContextWindow { get; init; }
}

public sealed record SessionEnded(int ExitCode, string? Error = null) : AgentEvent;

/// <summary>Anything the driver did not recognise. Kept so the UI can surface protocol drift.</summary>
public sealed record UnknownEvent(string RawType, string Raw) : AgentEvent;
