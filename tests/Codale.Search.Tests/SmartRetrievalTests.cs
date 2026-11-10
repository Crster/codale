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
    public async Task Source_changed_during_ranking_is_not_sent_from_the_old_basic_result()
    {
        var path = Path.Combine(_root, "Widget.cs");
        File.WriteAllText(path, "public class Widget { public void RenderWidget() { OldImplementation(); } }");
        await _index.RefreshAsync(_root);
        _index.Smart = new FakeSmart { Rank = async (_, _) =>
        {
            File.WriteAllText(path, "public class Widget { public void RenderWidget() { FreshImplementation(); } }");
            await _index.RefreshAsync(_root);
            return null;
        } };
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.DoesNotContain("OldImplementation", result.Text);
        Assert.Contains("FreshImplementation", result.Text);
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
        public IReadOnlyList<SmartEvidence> Evidence = [], Historical = [];
        public Func<IReadOnlyList<SmartCandidate>, CancellationToken, Task<IReadOnlyList<string>?>> Rank = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        public SmartStatus Status => new(SmartState.Ready, "Ready");
        public bool Thinking { get; set; }
        public event EventHandler? Changed { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SmartResult<string>?> SummarizeAsync(SmartRequestContext context, string path, CancellationToken ct = default) => Task.FromResult<SmartResult<string>?>(null);
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
    public async Task Current_and_background_evidence_reach_the_briefing_and_are_deduplicated()
    {
        var observation = new SmartEvidence("command", _root, "RenderWidget tests passed", DateTimeOffset.UtcNow.AddMinutes(-5), Command: "dotnet test", ExitCode: 0);
        _index.Smart = new FakeSmart { Rank = (c, _) => Task.FromResult<IReadOnlyList<string>?>(c.Select(x => x.Id).ToArray()), Evidence = [observation], Historical = [observation] };
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.Contains("RenderWidget tests passed", result.Text); Assert.Contains("Historical command observation", result.Text);
        Assert.Equal("test", result.SmartActivityId); Assert.Single(result.Keys);
        var next = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", result.Keys.ToHashSet(), null, null, default);
        Assert.DoesNotContain("RenderWidget tests passed", next.Text);
    }

    [Fact]
    public async Task Large_discoveries_fit_the_smart_budget_and_failures_keep_the_basic_budget()
    {
        File.WriteAllText(Path.Combine(_root, "Widget.cs"), "public class Widget { public void RenderWidget() {} }");
        await _index.RefreshAsync(_root);
        var smart = new FakeSmart
        {
            Rank = (c, _) => Task.FromResult<IReadOnlyList<string>?>(c.Select(x => x.Id).ToArray()),
            Evidence = Enumerable.Range(0, 8).Select(i => new SmartEvidence("command", _root, new string('水', 2000), DateTimeOffset.UtcNow.AddMinutes(-i), Command: "test" + i, ExitCode: 0)).ToArray()
        };
        _index.Smart = smart;
        var result = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) <= SmartContext.MaxBytes);
        Assert.Equal("Smart exploration", result.Method);
        smart.Rank = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        var fallback = await _index.RetrieveSmartAsync(_root, "RenderWidget", "", new HashSet<string>(), null, null, default);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fallback.Text) <= ProjectKnowledgeIndex.MaxContextBytes);
        Assert.Contains("RenderWidget", fallback.Text); Assert.Null(fallback.SmartActivityId);
    }
    public void Dispose()
    {
        _index.Dispose(); _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
