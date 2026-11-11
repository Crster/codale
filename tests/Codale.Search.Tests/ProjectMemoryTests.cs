using Codale.Core.Features;
using Codale.Db;

namespace Codale.Search.Tests;

/// <summary>What the project index keeps in its database: features, provider sessions, findings, learned associations and the source index.</summary>
public sealed class ProjectMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-memory-tests-").FullName;
    private string Folder => Path.Combine(_root, "project-db");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Separate_sessions_with_identical_topics_are_never_merged()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        Assert.Empty(index.ListFeatures());
        var first = index.CreateFeature("Fix login", "same topic");
        var second = index.CreateFeature("Fix login", "same topic");
        Assert.NotEqual(first.Id, second.Id);
        index.RecordProgress(first.Id, 10, "unrelated new task");
        Assert.Equal(2, index.ListFeatures().Count);
        Assert.Equal("same topic", index.GetFeature(first.Id)!.Description);
    }

    [Fact]
    public void A_feature_round_trips_and_lists_newest_first()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var login = index.CreateFeature("Login", "Sign in flow");
        Thread.Sleep(5);
        var dashboard = index.CreateFeature("Dashboard", "Charts");

        Assert.Equal([dashboard.Id, login.Id], index.ListFeatures().Select(f => f.Id));
        Assert.Equal("Sign in flow", index.GetFeature(login.Id)!.Description);
    }

    [Fact]
    public void Features_survive_reopening_the_database()
    {
        string id;
        using (var db = new CodaleDb(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
            id = index.CreateFeature("Login", "Sign in flow").Id;

        using var reopened = new CodaleDb(Folder);
        using var again = new ProjectKnowledgeIndex(reopened);
        Assert.Equal("Login", again.GetFeature(id)!.Name);
        Assert.Equal(id, again.SearchFeatures("login", 3).Single().Id);
    }

    [Fact]
    public void Search_finds_the_feature_by_its_name_summary_and_prompts()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var login = index.CreateFeature("Login", "");
        var dashboard = index.CreateFeature("Dashboard", "");
        index.SetSummary(login.Id, "Uses refresh tokens stored in the keychain", 10);
        index.RecordProgress(dashboard.Id, 100, "redesign the revenue chart layout");

        Assert.Equal(login.Id, index.SearchFeatures("the refresh token expires too early", 3).First().Id);
        Assert.Equal(dashboard.Id, index.SearchFeatures("chart layout looks off", 3).First().Id);
        Assert.Empty(index.SearchFeatures("zzz qqq", 3));
    }

    [Fact]
    public void Summary_and_progress_are_kept()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var f = index.CreateFeature("Login", "");
        index.RecordProgress(f.Id, 500, "fix redirect");
        index.SetSummary(f.Id, "## Done\nx", logOffset: 400);

        var read = index.GetFeature(f.Id)!;
        Assert.Equal(500, read.LogBytes);
        Assert.Equal(400, read.SummaryOffset);
        Assert.Equal("## Done\nx", read.Summary);

        index.SetSummary(f.Id, "## Done\ny", logOffset: 0, logBytes: 0);
        Assert.Equal(0, index.GetFeature(f.Id)!.LogBytes);
    }

    [Fact]
    public void Written_files_are_listed_newest_first_and_make_the_feature_findable_by_path()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var login = index.CreateFeature("Login", "");
        index.RecordSessionFile(login.Id, "src/Auth.cs");
        Thread.Sleep(5);
        index.RecordSessionFile(login.Id, "src/Session.cs");
        index.RecordProgress(login.Id, 1, null);

        Assert.Equal(["src/Session.cs", "src/Auth.cs"], index.FilesOf(login.Id, 10));
        Assert.Equal(["src/Session.cs"], index.FilesOf(login.Id, 1));
        Assert.Equal(2, index.FileCount(login.Id));
        Assert.Equal(login.Id, index.SearchFeatures("auth", 3).Single().Id);
    }

    [Fact]
    public void Deleting_a_feature_removes_it_its_files_and_its_search_entries()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var f = index.CreateFeature("Login", "");
        index.RecordSessionFile(f.Id, "src/Auth.cs");

        index.DeleteFeature(f.Id);

        Assert.Null(index.GetFeature(f.Id));
        Assert.Equal(0, index.FileCount(f.Id));
        Assert.Empty(index.SearchFeatures("login", 5));
        Assert.Empty(index.ListFeatures());
    }

    [Fact]
    public void Renaming_updates_what_search_finds()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var f = index.CreateFeature("Auth", "");
        index.RenameFeature(f.Id, "Authentication");

        Assert.Equal(f.Id, index.SearchFeatures("authentication", 3).Single().Id);
    }

    [Fact]
    public void Files_recorded_for_a_session_that_is_not_a_feature_do_not_appear_in_the_history()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        index.RecordSessionFile("global-search", "src/Auth.cs");
        index.RecordLearned("global-search", "w", "src/Auth.cs", "refresh the token", LearnedKind.Open);

        Assert.Empty(index.ListFeatures());
    }

    [Fact]
    public void A_stronger_association_replaces_a_weaker_one_and_never_the_other_way_round()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        index.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Read);
        index.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Edit);
        index.RecordLearned("s", "w", "a.cs", "fix the owner picker", LearnedKind.Read);
        index.RecordLearned("s", "w", "b.cs", "fix the owner picker", LearnedKind.Read);
        index.RecordLearned("s", "w", "b.cs", "fix the owner picker", LearnedKind.Open);

        var kinds = index.LearnedAssociations("w").ToDictionary(x => x.Path, x => x.Kind);
        Assert.Equal(2, kinds.Count);
        Assert.Equal(LearnedKind.Edit, kinds["a.cs"]);
        Assert.Equal(LearnedKind.Open, kinds["b.cs"]);
        Assert.Empty(index.LearnedAssociations("other"));
        index.RecordLearned("s", "w", "c.cs", "the", LearnedKind.Edit);   // no topic words: nothing to learn from
        Assert.Equal(2, index.LearnedAssociations("w").Count);
    }

    [Fact]
    public void Learned_associations_are_found_by_the_words_of_their_prompt_and_survive_reopening()
    {
        using (var db = new CodaleDb(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
            index.RecordLearned("s", "w", "src/Picker.cs", "show the projects owner picker", LearnedKind.Edit);

        using var reopened = new CodaleDb(Folder);
        using var again = new ProjectKnowledgeIndex(reopened);
        Assert.Equal("src/Picker.cs".Replace('/', Path.DirectorySeparatorChar),
            Assert.Single(again.SearchLearnedEdits("w", "owner picker")).Path);
        Assert.Empty(again.SearchLearnedEdits("other", "owner picker"));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void Explicit_provider_mapping_survives_restart_and_reconfiguration(string provider)
    {
        string sessionId;
        using (var db = new CodaleDb(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
        {
            sessionId = index.CreateFeature("First prompt", "").Id;
            index.SetProviderSession(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"));
            index.MarkContextDelivered("native", ["reference", "reference"]);
        }

        using var reopenedDb = new CodaleDb(Folder);
        using var reopened = new ProjectKnowledgeIndex(reopenedDb);
        Assert.Equal(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"), reopened.GetProviderSession(sessionId));
        Assert.Equal(["reference"], reopened.DeliveredContext("native"));
        reopened.ClearDeliveredContext("native");
        Assert.Empty(reopened.DeliveredContext("native"));
        reopened.SetProviderSession(new ProviderSession(sessionId, provider, "replacement", "checkout", "endpoint2"));
        Assert.Equal("replacement", reopened.GetProviderSession(sessionId)!.ProviderSessionId);
        Assert.Empty(reopened.DeliveredContext("replacement"));
        Assert.Single(reopened.ListFeatures());
    }

    [Fact]
    public void Deleting_a_session_removes_its_derived_memory_but_preserves_the_project_source_index()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var session = index.CreateFeature("Session", "");
        index.SetProviderSession(new ProviderSession(session.Id, "codex", "native", "checkout", ""));
        index.MarkContextDelivered("native", ["key"]);
        index.PutKnowledge(new KnowledgeHit("finding", session.Id, "codex", "checkout", "rev", "authentication finding", []));
        index.RecordLearned(session.Id, "checkout", "Auth.cs", "refresh the authentication token", LearnedKind.Edit);
        index.ReplaceSource("checkout", "Auth.cs", "hash", [(1, "class Authentication {}", "")]);

        index.DeleteFeature(session.Id);

        Assert.Null(index.GetProviderSession(session.Id));
        Assert.Empty(index.SearchKnowledge("authentication"));
        Assert.Empty(index.DeliveredContext("native"));
        Assert.Empty(index.LearnedAssociations("checkout"));
        Assert.Single(index.SearchSource("Authentication", "checkout"));
    }

    [Fact]
    public async Task Concurrent_worktrees_keep_all_source_and_findings()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        await Task.WhenAll(Task.Run(() => Write("one")), Task.Run(() => Write("two")));
        Assert.Equal(10, index.SourceHashes("one").Count);
        Assert.Equal(10, index.SourceHashes("two").Count);
        Assert.Equal(20, index.SearchKnowledge("finding", 30).Count);

        void Write(string root)
        {
            for (var i = 0; i < 10; i++)
            {
                index.ReplaceSource(root, $"File{i}.cs", $"hash{i}", [(1, "class Authentication {}", "")]);
                index.PutKnowledge(new KnowledgeHit(root + i, root, "codex", root, "rev", "finding Authentication", []));
            }
        }
    }

    [Fact]
    public void A_search_excluding_a_session_still_fills_the_limit_from_the_others()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        for (var i = 0; i < 5; i++)
            index.PutKnowledge(new KnowledgeHit("mine" + i, "me", "codex", "w", "r", "authentication finding " + i, []));
        index.PutKnowledge(new KnowledgeHit("theirs", "other", "codex", "w", "r", "authentication finding", []));

        var hits = index.SearchKnowledge("authentication", 3, excludeSessionId: "me");

        Assert.Equal("theirs", Assert.Single(hits).Id);
    }

    [Fact]
    public void Replacing_a_file_swaps_its_chunks_and_forgetting_it_removes_them()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        index.ReplaceSource("w", "Auth.cs", "h1", [(1, "old marker alpha", ""), (20, "other beta", "")]);
        index.ReplaceSource("w", "Auth.cs", "h2", [(1, "new marker gamma", "")]);

        Assert.Empty(index.SearchSource("alpha", "w"));
        var chunk = Assert.Single(index.SearchSource("gamma", "w"));
        Assert.Equal("h2", chunk.Hash);
        Assert.Equal("Auth.cs", chunk.Path);
        Assert.Equal("h2", index.SourceHashes("w")["Auth.cs"]);

        index.ReplaceSource("w", "Auth.cs", null, []);
        Assert.Empty(index.SearchSource("gamma", "w"));
        Assert.Empty(index.SourceHashes("w"));
        Assert.Empty(index.SourceChunksForPath("w", "Auth.cs"));
    }

    [Fact]
    public void Chunks_are_listed_in_line_order_and_ids_stay_unique_across_reopening()
    {
        long lastId;
        using (var db = new CodaleDb(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
        {
            index.ReplaceSource("w", "A.cs", "h", [(30, "third", ""), (2, "first", ""), (11, "second", "")]);
            var chunks = index.SourceChunksForPath("w", "A.cs", limit: 10);
            Assert.Equal([2, 11, 30], chunks.Select(c => c.StartLine));
            lastId = chunks.Max(c => c.Id);
        }

        using var reopened = new CodaleDb(Folder);
        using var again = new ProjectKnowledgeIndex(reopened);
        again.ReplaceSource("w", "B.cs", "h", [(1, "more", "")]);
        Assert.True(Assert.Single(again.SourceChunksForPath("w", "B.cs")).Id > lastId);
        Assert.Equal(3, again.SourceChunksForPath("w", "A.cs", limit: 10).Count);
    }

    [Fact]
    public void Chunk_ids_from_a_search_resolve_back_to_their_chunks_until_the_file_is_reindexed()
    {
        using var db = new CodaleDb(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        index.ReplaceSource("w", "A.cs", "h", [(1, "class Authentication {}", "Authentication")]);
        var found = Assert.Single(index.SearchSource("authentication", "w"));

        Assert.Equal(found, Assert.Single(index.SourceChunks([found.Id])));

        index.ReplaceSource("w", "A.cs", "h2", [(1, "class Other {}", "")]);
        Assert.Empty(index.SourceChunks([found.Id]));
    }
}
