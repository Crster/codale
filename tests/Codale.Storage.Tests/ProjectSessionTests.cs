using Codale.Core.Features;

namespace Codale.Storage.Tests;

public sealed class ProjectSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-session-tests-").FullName;
    private string Database => Path.Combine(_root, "project.db");
    [Fact]
    public void Separate_sessions_with_identical_topics_are_never_merged()
    {
        using var store = new FeatureStore(Database);
        Assert.Empty(store.List());
        var first = store.Create("Fix login", "same topic");
        var second = store.Create("Fix login", "same topic");
        Assert.NotEqual(first.Id, second.Id);
        store.RecordProgress(first.Id, 10, "unrelated new task");
        Assert.Equal(2, store.List().Count);
        Assert.Equal("same topic", store.Get(first.Id)!.Description);
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void Explicit_provider_mapping_survives_restart_and_reconfiguration(string provider)
    {
        string sessionId;
        using (var store = new FeatureStore(Database))
        {
            sessionId = store.Create("First prompt", "").Id;
            store.SetProviderSession(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"));
            store.MarkContextDelivered("native", ["reference", "reference"]);
        }
        using var reopened = new FeatureStore(Database);
        Assert.Equal(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"), reopened.GetProviderSession(sessionId));
        Assert.Equal(["reference"], reopened.DeliveredContext("native"));
        reopened.ClearDeliveredContext("native");
        Assert.Empty(reopened.DeliveredContext("native"));
        reopened.SetProviderSession(new ProviderSession(sessionId, provider, "replacement", "checkout", "endpoint2"));
        Assert.Equal("replacement", reopened.GetProviderSession(sessionId)!.ProviderSessionId);
        Assert.Empty(reopened.DeliveredContext("replacement"));
        Assert.Single(reopened.List());
    }
    [Fact]
    public void Deleting_a_session_removes_its_derived_memory_but_preserves_the_project_source_index()
    {
        using var store = new FeatureStore(Database);
        var session = store.Create("Session", "");
        store.SetProviderSession(new ProviderSession(session.Id, "codex", "native", "checkout", ""));
        store.MarkContextDelivered("native", ["key"]);
        store.PutKnowledge(new KnowledgeHit("finding", session.Id, "codex", "checkout", "rev", "authentication finding", []));
        store.ReplaceSource("checkout", "Auth.cs", "hash", [(1, "class Authentication {}")]);
        store.Delete(session.Id);
        Assert.Null(store.GetProviderSession(session.Id));
        Assert.Empty(store.SearchKnowledge("authentication"));
        Assert.Empty(store.DeliveredContext("native"));
        Assert.Single(store.SearchSource("Authentication", "checkout"));
    }

    [Fact]
    public async Task Concurrent_worktree_connections_keep_all_source_and_findings()
    {
        using var one = new FeatureStore(Database);
        using var two = new FeatureStore(Database);
        await Task.WhenAll(Task.Run(() => Write(one, "one")), Task.Run(() => Write(two, "two")));
        Assert.Equal(10, one.SourceHashes("one").Count);
        Assert.Equal(10, two.SourceHashes("two").Count);
        Assert.Equal(20, one.SearchKnowledge("finding", 30).Count);
        static void Write(FeatureStore store, string root)
        {
            for (var i = 0; i < 10; i++)
            {
                store.ReplaceSource(root, $"File{i}.cs", $"hash{i}", [(1, "class Authentication {}")]);
                store.PutKnowledge(new KnowledgeHit(root + i, root, "codex", root, "rev", "finding Authentication", []));
            }
        }
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
