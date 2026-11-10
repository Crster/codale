using Codale.Core.Features;
using Codale.Smart;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    /// <summary>Longest the model may spend reordering the index's candidates; past it the index order goes out as it is.</summary>
    private static readonly TimeSpan RankBudget = TimeSpan.FromSeconds(3);
    public ISmartExperience? Smart { get; set; }
    public SummaryCache? Summaries { get; set; }
    public string? SmartProjectRoot { get; set; }
    private readonly Dictionary<string, Task> _summaryTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether to pre-summarise files in the background at all; asked before every pass so a setting change applies at once.</summary>
    public Func<bool> SummariesEnabled { get; set; } = () => true;
    /// <summary>Most files one pass summarises. The rest wait for the next pass, so a large project is never worked through in one go.</summary>
    public int MaxSummariesPerPass { get; set; } = 3;
    public TimeSpan SummaryStartupDelay { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan SummaryPassInterval { get; set; } = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTimeOffset> _summaryPasses = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Raised from a background thread as a pass advances: files done and files it set out to do; (0, 0) when it ends.</summary>
    public event Action<int, int>? SummaryProgressChanged;

    private static readonly TimeSpan SummaryRetryAfter = TimeSpan.FromMinutes(30);
    private const int MaxConsecutiveSummaryFailures = 3;
    // A file the model could not summarise, by its content hash: not asked again until it changes or the wait passes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, DateTimeOffset At)> _summaryFailures = new(StringComparer.OrdinalIgnoreCase);

    private bool SummaryFailedRecently(string worktree, string path, string hash) =>
        _summaryFailures.TryGetValue(worktree + "|" + path, out var f) && f.Hash == hash && DateTimeOffset.UtcNow - f.At < SummaryRetryAfter;

    private void WarmSummaries(string worktree)
    {
        if (_disposed || Smart?.Status.State != SmartState.Ready || Summaries is null || !SummariesEnabled()) return;
        lock (_gate)
        {
            if (_summaryTasks.TryGetValue(worktree, out var task) && !task.IsCompleted) return;
            if (_summaryPasses.TryGetValue(worktree, out var last) && DateTimeOffset.UtcNow - last < SummaryPassInterval) return;
            _summaryPasses[worktree] = DateTimeOffset.UtcNow;
            _summaryTasks[worktree] = Task.Run(async () =>
            {
                int done = 0, total = 0;
                try
                {
                    // Let startup finish before spending CPU/GPU time on optional summaries.
                    await Task.Delay(SummaryStartupDelay, _stopping.Token).ConfigureAwait(false);
                    if (!SummariesEnabled() || Smart.Status.State != SmartState.Ready) return;
                    var paths = store.SourceHashes(worktree);
                    Summaries.Prune(worktree, paths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                    var thinking = Smart.Thinking;
                    var pending = paths.Where(p => Summaries.Get(worktree, p.Key, p.Value, thinking) is null && !SummaryFailedRecently(worktree, p.Key, p.Value))
                        .Take(MaxSummariesPerPass).ToList();
                    total = pending.Count;
                    int failures = 0;
                    foreach (var (path, hash) in pending)
                    {
                        _stopping.Token.ThrowIfCancellationRequested();
                        if (done > 0) await Task.Delay(SummaryInterval, _stopping.Token).ConfigureAwait(false);
                        if (!SummariesEnabled() || Smart.Status.State != SmartState.Ready) break;
                        SummaryProgressChanged?.Invoke(done, total);
                        var source = ReadSource(worktree, path);
                        if (source is null || Hash(source) != hash) { done++; continue; }
                        var summary = await Smart.SummarizeAsync(new(SmartProjectRoot ?? worktree, worktree, "summary",
                            Deadline: DateTimeOffset.UtcNow.AddSeconds(20)), path, _stopping.Token).ConfigureAwait(false);
                        done++;
                        if (!string.IsNullOrWhiteSpace(summary?.Value)
                            && summary.Evidence.Any(e => e.Kind == "file" && string.Equals(e.Path, Path.GetFullPath(path, worktree), StringComparison.OrdinalIgnoreCase))
                            && ReadSource(worktree, path) is { } current && Hash(current) == hash)
                        {
                            Summaries.Put(worktree, path, hash, summary.Value!, thinking, summary.Evidence);
                            failures = 0;
                        }
                        else
                        {
                            // Remember the miss so the next pass moves on instead of repeating this file forever.
                            _summaryFailures[worktree + "|" + path] = (hash, DateTimeOffset.UtcNow);
                            if (++failures >= MaxConsecutiveSummaryFailures) break;
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
                finally { SummaryProgressChanged?.Invoke(0, 0); }
            });
        }
    }

    public async Task<RetrievalResult> RetrieveSmartAsync(string worktree, string prompt, string recent,
        IReadOnlySet<string> delivered, string? currentSessionId, IReadOnlyList<string>? workingSet,
        CancellationToken ct, DateTimeOffset? deadline = null, string? chatId = null, SmartIntent? intent = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // The index answers on its own, as pointers (ranges to open, never the code). The model only reorders them,
        // within a short budget; if it is slow or fails, the index order goes out as it is.
        var basicTask = Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet, pointersOnly: true));
        if (Smart?.Status.State != SmartState.Ready || ct.IsCancellationRequested) return await basicTask.ConfigureAwait(false);
        try
        {
            var chunksTask = Task.Run(() => Search(worktree, prompt, recent, 8, 2), ct);
            var chunks = await chunksTask.ConfigureAwait(false);
            if (chunks.Count < 2) return await basicTask.ConfigureAwait(false); // nothing to reorder
            var candidates = chunks.Select(c => new SmartCandidate(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Path)).ToList();
            var budget = DateTimeOffset.UtcNow.Add(RankBudget);
            var context = new SmartRequestContext(SmartProjectRoot ?? worktree, worktree, "context", chatId ?? currentSessionId,
                deadline is { } d && d < budget ? d : budget);
            var ranked = await Smart.RankAsync(context, prompt, candidates, ct).WaitAsync(ct).ConfigureAwait(false);
            if (ranked?.Value is null || ct.IsCancellationRequested || Smart.Status.State != SmartState.Ready) return await basicTask.ConfigureAwait(false);
            var fresh = await Task.Run(() => Retrieve(worktree, prompt, recent, delivered, currentSessionId, workingSet,
                preferredChunkIds: ranked.Value, pointersOnly: true)).ConfigureAwait(false);
            return fresh with { Method = "Smart rerank", SmartActivityId = ranked.ActivityId, ElapsedMilliseconds = timer.ElapsedMilliseconds };
        }
        catch (Exception) { return await basicTask.ConfigureAwait(false); }
    }
}
