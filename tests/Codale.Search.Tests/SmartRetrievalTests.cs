using Codale.Smart;
using Codale.Storage;

namespace Codale.Search.Tests;

public sealed class SmartRetrievalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-smart-retrieval-").FullName;
    private readonly FeatureStore _store;
    private readonly ProjectKnowledgeIndex _index;
    public SmartRetrievalTests()
    {
        _store = new FeatureStore(Path.Combine(_root, "knowledge.db"));
        _index = new ProjectKnowledgeIndex(_store);
    }
    [Fact]
    public async Task A_model_that_ignores_cancellation_cannot_hold_up_basic_retrieval()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget { public void RenderWidget() {} }");
        await _index.RefreshAsync(_root);
        _index.Smart = new FakeSmart { Rank = (_, _) => new TaskCompletionSource<IReadOnlyList<string>?>().Task };
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, budget.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("Widget.cs", result.Files);
        Assert.Contains("RenderWidget", result.Text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) <= ProjectKnowledgeIndex.MaxContextBytes);
    }
    [Fact]
    public async Task Model_order_is_applied_to_fresh_context()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "public class Alpha { public void RenderWidget() {} }");
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "public class Beta { public void RenderWidget() {} }");
        await _index.RefreshAsync(_root);
        string? expected = null;
        _index.Smart = new FakeSmart { Rank = (candidates, _) =>
        {
            var reversed = candidates.Reverse().ToList(); expected = reversed[0].Path;
            return Task.FromResult<IReadOnlyList<string>?>(reversed.Select(c => c.Id).ToArray());
        } };
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.Equal(expected, result.Files[0]);
    }
    private sealed class FakeSmart : ISmartExperience
    {
        public Func<SmartRequestContext, string, CancellationToken, Task<SmartResult<string>?>> Summary = (_, _, _) => Task.FromResult<SmartResult<string>?>(null);
        public IReadOnlyList<SmartEvidence> Evidence = [], Historical = [];
        public Func<IReadOnlyList<SmartCandidate>, CancellationToken, Task<IReadOnlyList<string>?>> Rank = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Func<string, string?> Brief = _ => null;
        public int Briefs;
        public Task<SmartResult<string>?> BriefAsync(SmartRequestContext context, string request, SmartIntent intent, string material, CancellationToken ct = default)
        {
            Briefs++;
            var value = Brief(material);
            return Task.FromResult<SmartResult<string>?>(value is null ? null : new(value, [], "brief"));
        }
        public SmartStatus Status => new(SmartState.Ready, "Ready");
        public bool Thinking { get; set; }
        public event EventHandler? Changed { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SmartResult<string>?> SummarizeAsync(SmartRequestContext context, string path, CancellationToken ct = default) => Summary(context, path, ct);
        public async Task<SmartResult<IReadOnlyList<string>>?> RankAsync(SmartRequestContext context, string query, IReadOnlyList<SmartCandidate> candidates, CancellationToken ct = default)
        {
            var value = await Rank(candidates, ct);
            return value is null ? null : new(value, Evidence, "test");
        }
        public Task<SmartResult<SmartIntent?>?> ClassifyAsync(SmartRequestContext context, string message, string? previousReply, CancellationToken ct = default) => Task.FromResult<SmartResult<SmartIntent?>?>(null);
        public Task<SmartResult<string>?> CommitMessageAsync(SmartRequestContext context, CancellationToken ct = default) => Task.FromResult<SmartResult<string>?>(null);
        public Task<IReadOnlyList<SmartEvidence>> DiscoveriesAsync(SmartRequestContext context, string query, CancellationToken ct = default) => Task.FromResult(Historical);
    }
    [Fact]
    public async Task Smart_context_is_pointers_not_code_and_reflects_the_model_order()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget { public void RenderWidget() {} }");
        await _index.RefreshAsync(_root);
        _index.Smart = new FakeSmart { Rank = (c, _) => Task.FromResult<IReadOnlyList<string>?>(c.Select(x => x.Id).ToArray()) };
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.Contains("Widget.cs", result.Text);
        Assert.DoesNotContain("```", result.Text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) <= ProjectKnowledgeIndex.MaxContextBytes);
    }

    [Fact]
    public async Task A_failed_or_slow_rank_falls_back_to_the_index_order_and_no_brief_is_requested()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget { public void RenderWidget() {} }");
        File.WriteAllText(Path.Combine(_root, "Gadget.cs"), "public class Gadget { public void RenderWidget() {} }");
        await _index.RefreshAsync(_root);
        var smart = new FakeSmart { Rank = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null) };
        _index.Smart = smart;
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default, intent: SmartIntent.Act);
        Assert.Null(result.SmartActivityId);
        Assert.Contains("RenderWidget", result.Text);
        Assert.Equal(0, smart.Briefs);
        smart.Rank = (c, _) => Task.FromResult<IReadOnlyList<string>?>(c.Select(x => x.Id).ToArray());
        var ranked = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default, intent: SmartIntent.Act);
        Assert.Equal("Smart rerank", ranked.Method);
        Assert.Equal(0, smart.Briefs);
    }
    [Fact]
    public async Task Indexing_reports_busy_then_idle_even_when_the_snapshot_is_reused()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget {}");
        var states = new List<bool>();
        _index.IndexingChanged += () => states.Add(_index.IsIndexing);
        await _index.RefreshAsync(_root);
        await _index.RefreshAsync(_root);
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public async Task Summary_pass_is_bounded_and_repeated_refreshes_do_not_restart_it()
    {
        for (int i = 0; i < 6; i++) File.WriteAllText(Path.Combine(_root, $"Widget{i}.cs"), $"public class Widget{i} {{}}");
        int calls = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _index.SummaryStartupDelay = TimeSpan.Zero;
        _index.SummaryInterval = TimeSpan.Zero;
        _index.Summaries = new SummaryCache(Path.Combine(_root, "summaries"));
        _index.Smart = new FakeSmart { Summary = (context, relative, _) =>
        {
            Assert.True(context.Deadline <= DateTimeOffset.UtcNow.AddSeconds(20));
            Interlocked.Increment(ref calls);
            var path = Path.Combine(_root, relative);
            var source = File.ReadAllText(path);
            return Task.FromResult<SmartResult<string>?>(new("A widget class.",
                [new("file", path, source, DateTimeOffset.UtcNow, SummaryCache.Hash(source), 1, 1)], "summary"));
        } };
        _index.SummaryProgressChanged += (_, total) => { if (total == 0) ended.TrySetResult(); };
        await _index.RefreshAsync(_root);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, calls);
        await _index.RefreshAsync(_root);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Closing_during_startup_delay_cancels_optional_work()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget {}");
        int calls = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _index.SummaryStartupDelay = TimeSpan.FromMinutes(1);
        _index.Summaries = new SummaryCache(Path.Combine(_root, "summaries"));
        _index.Smart = new FakeSmart { Summary = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<SmartResult<string>?>(null);
        } };
        _index.SummaryProgressChanged += (_, total) => { if (total == 0) ended.TrySetResult(); };
        await _index.RefreshAsync(_root);
        _index.Dispose();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        _index.Dispose(); _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
