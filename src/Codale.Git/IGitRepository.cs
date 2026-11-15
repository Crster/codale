namespace Codale.Git;

/// <summary>Git operations for one working directory.</summary>
public interface IGitRepository
{
    Task<StagedDiffSnapshot> GetStagedDiffAsync(CancellationToken ct = default);
    Task<GitSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileDiff>> GetWorkingTreeDiffAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileDiff>> GetFileDiffAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileDiff>> GetCommitDiffAsync(string sha, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> StageAsync(string path, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> StageAllAsync(CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> UnstageAsync(string path, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> DiscardAsync(string path, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> IgnoreAsync(string path, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CommitAsync(string message, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CheckoutAsync(string branch, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CreateBranchAsync(string name, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CreateBranchAtAsync(string name, string sha, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CheckoutCommitAsync(string sha, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> CherryPickAsync(string sha, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> RevertAsync(string sha, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> ResetAsync(string sha, string mode, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> PushAsync(string branch, bool hasUpstream, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> PullAsync(CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> InitAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> StashPushAsync(string? message, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> StashPopAsync(string stashRef, CancellationToken ct = default);
    Task<GitRepository.GitWriteResult> StashDropAsync(string stashRef, CancellationToken ct = default);
}

public interface IGitRepositoryFactory
{
    IGitRepository Create(string workingDirectory);
}

public sealed class GitRepositoryFactory : IGitRepositoryFactory
{
    public IGitRepository Create(string workingDirectory) => new GitRepository(workingDirectory);
}