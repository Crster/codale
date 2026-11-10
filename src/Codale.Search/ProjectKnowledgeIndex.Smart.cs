using Codale.Core.Features;
using Codale.Smart;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    /// <summary>Longest the model may spend writing the brief; past it the verified context goes out as it is.</summary>
    private static readonly TimeSpan BriefBudget = TimeSpan.FromSeconds(12);
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
            // Auto mode already read the request's intent. The model turns the verified material into a brief of
            // where to act and what to respect, so the agent has an answer to follow rather than a list to search.
            // Nothing new to say (everything already delivered) means no brief, and a slow or unverifiable one is dropped.
            var text = discovered.Text;
            if (intent is { } routed && text.Length > 0)
            {
                var budget = DateTimeOffset.UtcNow.Add(BriefBudget);
                var briefContext = context with { Deadline = deadline is { } d && d < budget ? d : budget };
                try
                {
                    var brief = await Smart.BriefAsync(briefContext, prompt, routed, text, ct).WaitAsync(ct).ConfigureAwait(false);
                    if (brief?.Value is { Length: > 0 } written) text = written + "\n\n" + text;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            return fresh with { Text = text, Keys = discovered.Keys, Tokens = System.Text.Encoding.UTF8.GetByteCount(text),
                Files = discovered.Files.Concat(includesBasic ? fresh.Files : []).Distinct().ToArray(), Snippets = discovered.Snippets + (includesBasic ? fresh.Snippets : 0),
                Findings = discovered.Findings + (includesBasic ? fresh.Findings : 0), Method = "Smart exploration", SmartActivityId = ranked.ActivityId,
                ElapsedMilliseconds = timer.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) { return await BasicAsync().ConfigureAwait(false); }
        catch (Exception) { return await BasicAsync().ConfigureAwait(false); }
    }
}
