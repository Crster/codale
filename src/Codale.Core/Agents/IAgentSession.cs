using System.Threading.Channels;

namespace Codale.Core.Agents;

public enum ApprovalBehavior
{
    Allow,
    Deny,
}

/// <summary>How the host answered an <see cref="ApprovalRequested"/>.</summary>
public sealed record ApprovalDecision
{
    public required ApprovalBehavior Behavior { get; init; }

    /// <summary>Shown to the model when denying, so it can adapt rather than just fail.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Optional edited tool input. Lets the user tweak a command before allowing it;
    /// when null the original input is echoed back unchanged.
    /// </summary>
    public System.Text.Json.JsonElement? UpdatedInput { get; init; }

    /// <summary>A <see cref="PermissionSuggestion"/> the user accepted along with the decision.</summary>
    public PermissionSuggestion? AcceptedSuggestion { get; init; }

    /// <summary>
    /// A permission mode the host itself chose with this answer - the plan flow's
    /// "accept into edit/auto mode" buttons. Unlike an accepted suggestion (the CLI's
    /// own proposal, already applied host-side) the caller must switch the live
    /// session over explicitly.
    /// </summary>
    public string? SwitchToMode { get; init; }

    public static ApprovalDecision Allow() => new() { Behavior = ApprovalBehavior.Allow };

    public static ApprovalDecision Deny(string? message = null) =>
        new() { Behavior = ApprovalBehavior.Deny, Message = message };
}

/// <summary>
/// One user turn: text plus optional attachments. <see cref="PlanOnly"/> asks the
/// agent to plan rather than edit - providers with a real plan mode switch into it;
/// the rest phrase it as an instruction.
/// </summary>
public sealed record UserTurn(string Text)
{
    public IReadOnlyList<TurnAttachment> Attachments { get; init; } = [];

    public bool PlanOnly { get; init; }

    /// <summary>
    /// Ask mode: the agent explains rather than acts. Every provider phrases it as
    /// <see cref="AskInstruction"/> on the turn, and the host denies any tool that
    /// would change something, so the instruction is backed by enforcement.
    /// </summary>
    public bool AskOnly { get; init; }

    /// <summary>The prefix an ask-mode turn carries; history strips it back off.</summary>
    public const string AskInstruction =
        "[Ask mode] Answer this with a detailed explanation only. You may read files, search the " +
        "project and search or fetch from the web to ground the answer, but do not edit, create or " +
        "delete files and do not run commands that change anything. Explain what you would do and why, " +
        "with code snippets where they help.\n\n";

    private const string AskMarker = "[Ask mode] ";

    /// <summary>
    /// A forked session's briefing (<see cref="SessionForking.ContextBlock"/>), carried on its
    /// first turn only. Hidden from the transcript on replay, like the ask-mode prefix.
    /// </summary>
    public string? ContextPrefix { get; init; }

    /// <summary>The text as sent to the provider: the fork briefing and ask-mode prefix folded in when set.</summary>
    public string ProviderText => (ContextPrefix ?? "") + (AskOnly ? AskInstruction + Text : Text);

    /// <summary>
    /// The user's own words from a recorded prompt: any ask-mode prefix removed. Matched
    /// by its marker up to the blank line, so prompts recorded under an older wording of
    /// the instruction come back clean too.
    /// </summary>
    public static string StripHostInstructions(string text)
    {
        text = SessionForking.StripContext(text);
        if (!text.StartsWith(AskMarker, StringComparison.Ordinal))
        {
            return text;
        }

        var normalized = text.ReplaceLineEndings("\n");
        var end = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? text : normalized[(end + 2)..];
    }
}

/// <summary>
/// A live conversation with one agent CLI, scoped to one working directory.
/// Implementations own a child process and must kill it on dispose.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Provider-assigned id, used to resume this conversation later.</summary>
    string? SessionId { get; }

    /// <summary>Normalised event stream. Completes when the session ends.</summary>
    ChannelReader<AgentEvent> Events { get; }

    Task StartAsync(CancellationToken ct = default);

    Task SendAsync(UserTurn turn, CancellationToken ct = default);

    /// <summary>Stop the current turn without ending the session.</summary>
    Task InterruptAsync(CancellationToken ct = default);

    /// <summary>
    /// Switch the live session's permission mode (e.g. "plan") when the CLI supports
    /// a mid-session switch. Returns false when the provider has none - the caller
    /// then falls back to phrasing the request - and never throws for unsupported.
    /// </summary>
    Task<bool> TrySetPermissionModeAsync(string mode, CancellationToken ct = default);

    /// <summary>
    /// Switch the live session's model and/or reasoning effort without a restart,
    /// when the CLI can do it: Claude has set_model / apply_flag_settings control requests. Null resets
    /// that field to the CLI's default. Returns false when the provider cannot
    /// express the change live - the caller then restarts the session - and never
    /// throws for unsupported.
    /// </summary>
    Task<bool> TrySetModelEffortAsync(string? model, string? effort, CancellationToken ct = default);

    /// <summary>Answer a pending <see cref="ApprovalRequested"/>. The turn is blocked until this lands.</summary>
    Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct = default);

    /// <summary>
    /// Models the CLI offers, for the session card's picker. Empty when the CLI has no
    /// catalogue and none is documented. Never throws for "not supported": a failed
    /// query is an empty list.
    /// </summary>
    Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// The background shells and subagents the provider's runtime is tracking, when it
    /// exposes them. Empty when it does not - the transcript's own tool
    /// calls are then the only source. Never throws: a failed query is an empty list.
    /// </summary>
    Task<IReadOnlyList<AgentTaskInfo>> ListTasksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentTaskInfo>>([]);

    /// <summary>
    /// A snapshot of a runtime-tracked task's recent output: a shell's stdout/stderr, or
    /// a subagent's latest activity lines. Replaces, never appends - the runtime keeps
    /// only a tail. Null when unavailable.
    /// </summary>
    Task<string?> GetTaskOutputAsync(string taskId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>Stop one runtime-tracked task by its <see cref="AgentTaskInfo.Id"/>. False when it could not be stopped.</summary>
    Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
}

public enum AgentTaskKind
{
    Shell,
    Subagent,
}

public enum AgentTaskState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One runtime-tracked long-running task, as of the moment it was listed.</summary>
public sealed record AgentTaskInfo
{
    public required string Id { get; init; }

    public required AgentTaskKind Kind { get; init; }

    public required AgentTaskState State { get; init; }

    /// <summary>The command line, or the subagent's description.</summary>
    public string Title { get; init; } = "";

    /// <summary>File the runtime streams a detached shell's output into.</summary>
    public string? LogPath { get; init; }

    public int? Pid { get; init; }

    /// <summary>The tool call that started it, when the runtime says which.</summary>
    public string? ToolCallId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>A subagent's most recent reply, or its failure message.</summary>
    public string? LatestText { get; init; }
}
