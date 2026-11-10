using Codale.Core.Features;
using Codale.Smart;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    public ISmartExperience? Smart { get; set; }
    public SummaryCache? Summaries { get; set; }
    public string? SmartProjectRoot { get; set; }
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
                        var thinking = Smart.Thinking;
                        if (Summaries.Get(worktree, path, hash, thinking) is not null) continue;
                        var source = ReadSource(worktree, path);
                        if (source is null || Hash(source) != hash) continue;
                        var summary = await Smart.SummarizeAsync(new(SmartProjectRoot ?? worktree, worktree, "summary"), path, _stopping.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(summary?.Value)) break;
                        if (summary.Evidence.Any(e => e.Kind == "file" && string.Equals(e.Path, Path.GetFullPath(path, worktree), StringComparison.OrdinalIgnoreCase))
                            && ReadSource(worktree, path) is { } current && Hash(current) == hash)
                            Summaries.Put(worktree, path, hash, summary.Value!, thinking, summary.Evidence);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            });
        }
    }

    public async Task<RetrievalResult> RetrieveSmartAsync(string worktree, string prompt, string recent,
        IReadOnlySet<string> delivered, string? currentSessionId, IReadOnlyList<string>? workingSet,
        CancellationToken ct, DateTimeOffset? deadline = null, string? chatId = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // Always retain a complete basic result before attempting model inference.
        Task<RetrievalResult> BasicAsync() => Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet));
        var basic = await BasicAsync().ConfigureAwait(false);
        if (Smart?.Status.State != SmartState.Ready || ct.IsCancellationRequested) return basic;
        try
        {
            var chunks = await Task.Run(() => Search(worktree, prompt, recent, 8, 2), ct).ConfigureAwait(false);
            var candidates = chunks.Select(c => new SmartCandidate(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Path)).ToList();
            var context = new SmartRequestContext(SmartProjectRoot ?? worktree, worktree, "context", chatId ?? currentSessionId, deadline);
            var ranked = await Smart.RankAsync(context, prompt, candidates, ct).WaitAsync(ct).ConfigureAwait(false);
            if (ranked?.Value is null || ct.IsCancellationRequested || Smart.Status.State != SmartState.Ready) return await BasicAsync().ConfigureAwait(false);
            // Retrieve rebuilds the context with fresh, hash-verified source and the existing byte limits.
            var fresh = await Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet,
                preferredChunkIds: ranked.Value)).ConfigureAwait(false);
            var historical = await Smart.DiscoveriesAsync(context, prompt, ct).ConfigureAwait(false);
            var discovered = await SmartContext.BuildAsync(ranked.Evidence.Concat(historical), delivered, fresh.Text, fresh.Keys, ct).ConfigureAwait(false);
            var includesBasic = fresh.Keys.Count > 0 && fresh.Keys.All(discovered.Keys.Contains);
            return fresh with { Text = discovered.Text, Keys = discovered.Keys, Tokens = System.Text.Encoding.UTF8.GetByteCount(discovered.Text),
                Files = discovered.Files.Concat(includesBasic ? fresh.Files : []).Distinct().ToArray(), Snippets = discovered.Snippets + (includesBasic ? fresh.Snippets : 0),
                Findings = discovered.Findings + (includesBasic ? fresh.Findings : 0), Method = "Smart exploration", SmartActivityId = ranked.ActivityId,
                ElapsedMilliseconds = timer.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) { return await BasicAsync().ConfigureAwait(false); }
        catch (Exception) { return await BasicAsync().ConfigureAwait(false); }
    }
}
