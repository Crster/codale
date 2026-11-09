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
        public Func<IReadOnlyList<SmartCandidate>, CancellationToken, Task<IReadOnlyList<string>?>> Rank = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        public SmartStatus Status => new(SmartState.Ready, "Ready");
        public event EventHandler? Changed { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> SummarizeAsync(string path, string source, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> RankAsync(string query, IReadOnlyList<SmartCandidate> candidates, CancellationToken ct = default) => Rank(candidates, ct);
        public Task<SmartIntent?> ClassifyAsync(string message, string? previousReply, CancellationToken ct = default) => Task.FromResult<SmartIntent?>(null);
        public Task<string?> CommitMessageAsync(string stagedDiff, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
    public void Dispose()
    {
        _index.Dispose(); _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
