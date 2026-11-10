namespace Codale.Smart;

public enum SmartIntent { Ask, Plan, Act }
public enum SmartState { Disabled, Downloading, Loading, Ready, Error }
public sealed record SmartStatus(SmartState State, string Message, double Progress = 0, string? Backend = null);
public sealed record SmartCandidate(string Id, string Path);
public sealed record SmartCommandAssessment(string Description, string Risk, string Reason);

public sealed record SmartRequestContext(string Project, string Worktree, string Task, string? ChatId = null,
    DateTimeOffset? Deadline = null)
{
    public string WorkingDirectory => Path.GetFullPath(string.IsNullOrWhiteSpace(Worktree) ? Project : Worktree);
}

public sealed record SmartResult<T>(T? Value, IReadOnlyList<SmartEvidence> Evidence, string ActivityId);
public sealed record SmartEvidence(string Kind, string Path, string Text, DateTimeOffset ObservedAt,
    string? Hash = null, int StartLine = 0, int EndLine = 0, string? Command = null, int? ExitCode = null)
{
    public string Key => Kind == "file" ? $"smart:file:{Path}:{Hash}:{StartLine}:{EndLine}"
        : $"smart:command:{Path}:{ObservedAt:O}:{Command}";
    public Task<bool> IsFreshAsync(CancellationToken ct = default) => SmartTools.FreshAsync(this, ct);
}
public sealed record SmartToolActivity(string Tool, string Arguments, string WorkingDirectory,
    DateTimeOffset StartedAt, string Output = "", int? ExitCode = null, long ElapsedMilliseconds = 0,
    string State = "running");
public sealed record SmartActivity(string Id, SmartRequestContext Context, DateTimeOffset StartedAt,
    string State = "queued", IReadOnlyList<SmartToolActivity>? Tools = null,
    IReadOnlyList<SmartEvidence>? Evidence = null, string? Error = null, long ElapsedMilliseconds = 0);

public interface ISmartExperience
{
    Task<SmartResult<SmartCommandAssessment>?> AssessCommandAsync(SmartRequestContext context, string command,
        string baseline, CancellationToken ct = default) => Task.FromResult<SmartResult<SmartCommandAssessment>?>(null);
    SmartStatus Status { get; }
    event EventHandler? Changed;
    bool Thinking { get; set; }
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task<SmartResult<string>?> SummarizeAsync(SmartRequestContext context, string path, CancellationToken ct = default);
    Task<SmartResult<IReadOnlyList<string>>?> RankAsync(SmartRequestContext context, string query, IReadOnlyList<SmartCandidate> candidates, CancellationToken ct = default);
    Task<SmartResult<SmartIntent?>?> ClassifyAsync(SmartRequestContext context, string message, string? previousReply, CancellationToken ct = default);
    Task<SmartResult<string>?> CommitMessageAsync(SmartRequestContext context, CancellationToken ct = default);
    Task<SmartResult<string>?> ShellCommandAsync(SmartRequestContext context, string request, string shell, CancellationToken ct = default) =>
        Task.FromResult<SmartResult<string>?>(null);
    Task<IReadOnlyList<SmartEvidence>> DiscoveriesAsync(SmartRequestContext context, string query, CancellationToken ct = default);
    /// <summary>The task brief for the agent CLI (see <see cref="SmartBrief"/>), written from verified <paramref name="material"/>; null when unavailable.</summary>
    Task<SmartResult<string>?> BriefAsync(SmartRequestContext context, string request, SmartIntent intent, string material, CancellationToken ct = default) =>
        Task.FromResult<SmartResult<string>?>(null);
}
