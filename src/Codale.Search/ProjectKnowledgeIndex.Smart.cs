using Codale.Core.Features;
using Codale.Smart;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    public ISmartExperience? Smart { get; set; }
    public SummaryCache? Summaries { get; set; }
    private readonly Dictionary<string, Task> _summaryTasks = new(StringComparer.OrdinalIgnoreCase);

    private void WarmSummaries(string worktree)
    {
        if (_disposed || Smart?.Status.State != SmartState.Ready || Summaries is null) return;
        lock (_gate)
        {
            if (_summaryTasks.TryGetValue(worktree, out var task) && !task.IsCompleted) return;
            _summaryTasks[worktree] = Task.Run(async () =>
            {
                try
                {
                    var paths = store.SourceHashes(worktree);
                    Summaries.Prune(worktree, paths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                    foreach (var (path, hash) in paths)
                    {
                        _stopping.Token.ThrowIfCancellationRequested();
                        if (Smart.Status.State != SmartState.Ready) break;
                        if (Summaries.Get(worktree, path, hash) is not null) continue;
                        var source = ReadSource(worktree, path);
                        if (source is null || Hash(source) != hash) continue;
                        var summary = await Smart.SummarizeAsync(path, source, _stopping.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(summary)) break;
                        if (ReadSource(worktree, path) is { } current && Hash(current) == hash)
                            Summaries.Put(worktree, path, hash, summary);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            });
        }
    }

    public async Task<RetrievalResult> RetrieveSmartAsync(string worktree, string prompt, string recent,
        IReadOnlySet<string> delivered, string? currentSessionId, IReadOnlyList<string>? workingSet,
        CancellationToken ct)
    {
        // Always retain a complete basic result before attempting model inference.
        Task<RetrievalResult> BasicAsync() => Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet));
        var basic = await BasicAsync().ConfigureAwait(false);
        if (Smart?.Status.State != SmartState.Ready || ct.IsCancellationRequested) return basic;
        try
        {
            var chunks = await Task.Run(() => Search(worktree, prompt, recent, 8, 2), ct).ConfigureAwait(false);
            var candidates = chunks.Select(c => new SmartCandidate(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Path,
                c.Text.Length > 700 ? c.Text[..700] : c.Text, Summaries?.Get(worktree, c.Path, c.Hash))).ToList();
            var ranked = await Smart.RankAsync(prompt, candidates, ct).WaitAsync(ct).ConfigureAwait(false);
            if (ranked is null || ct.IsCancellationRequested || Smart.Status.State != SmartState.Ready) return await BasicAsync().ConfigureAwait(false);
            // Retrieve rebuilds the context with fresh, hash-verified source and the existing byte limits.
            return await Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet,
                preferredChunkIds: ranked)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return await BasicAsync().ConfigureAwait(false); }
        catch (Exception) { return await BasicAsync().ConfigureAwait(false); }
    }
}
