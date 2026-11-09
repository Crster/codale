namespace Codale.Smart;

public enum SmartIntent { Ask, Plan, Act }
public enum SmartState { Disabled, Downloading, Loading, Ready, Error }
public sealed record SmartStatus(SmartState State, string Message, double Progress = 0, string? Backend = null);
public sealed record SmartCandidate(string Id, string Path, string Excerpt, string? Summary = null);

public interface ISmartExperience
{
    SmartStatus Status { get; }
    event EventHandler? Changed;
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task<string?> SummarizeAsync(string path, string source, CancellationToken ct = default);
    Task<IReadOnlyList<string>?> RankAsync(string query, IReadOnlyList<SmartCandidate> candidates, CancellationToken ct = default);
    Task<SmartIntent?> ClassifyAsync(string message, string? previousReply, CancellationToken ct = default);
    Task<string?> CommitMessageAsync(string stagedDiff, CancellationToken ct = default);
}
