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
    [Fact]
    public void An_index_from_before_chunks_had_symbols_is_upgraded_in_place()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE source_chunks (id INTEGER PRIMARY KEY, worktree TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL, start_line INTEGER NOT NULL, text TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new FeatureStore(Database);
        store.ReplaceSource("checkout", "Auth.cs", "hash", [(1, "class Authentication {}", "Authentication")]);
        var chunk = Assert.Single(store.SourceChunksForPath("checkout", "Auth.cs"));
        Assert.Equal("Authentication", chunk.Symbols);
        Assert.Single(store.SearchSourceTerms("authentication", "checkout"));
    }

    [Fact]
    public void A_stronger_association_replaces_a_weaker_one_and_never_the_other_way_round()
    {
        using var store = new FeatureStore(Database);
        store.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Read);
        store.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Edit);
        store.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Read);
        store.RecordLearned("s", "w", "b.cs", "fix the owner picker", LearnedKind.Read);
        store.RecordLearned("s", "w", "b.cs", "fix the owner picker", LearnedKind.Open);

        var kinds = store.LearnedAssociations("w").ToDictionary(x => x.Path, x => x.Kind);
        Assert.Equal(2, kinds.Count);
        Assert.Equal(LearnedKind.Edit, kinds["a.cs"]);
        Assert.Equal(LearnedKind.Open, kinds["b.cs"]);
        Assert.Empty(store.LearnedAssociations("other"));
        store.RecordLearned("s", "w", "c.cs", "the", LearnedKind.Edit);   // no topic words: nothing to learn from
        Assert.Equal(2, store.LearnedAssociations("w").Count);
    }

    [Fact]
    public void Learned_associations_from_before_kinds_existed_are_edits()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE learned_edits (id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, worktree TEXT NOT NULL, path TEXT NOT NULL, prompt TEXT NOT NULL, UNIQUE(session_id, path, prompt)); " +
                                  "INSERT INTO learned_edits(session_id,worktree,path,prompt) VALUES ('s','w','Old.cs','old request')";
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new FeatureStore(Database);
        Assert.Equal(("Old.cs", "old request", LearnedKind.Edit), Assert.Single(store.LearnedAssociations("w")));
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
        store.ReplaceSource("checkout", "Auth.cs", "hash", [(1, "class Authentication {}", "")]);
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
                store.ReplaceSource(root, $"File{i}.cs", $"hash{i}", [(1, "class Authentication {}", "")]);
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
