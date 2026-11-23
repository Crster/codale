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
        using var db = new CodaleDatabase(Folder);
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
        using var db = new CodaleDatabase(Folder);
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
        using (var db = new CodaleDatabase(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
            id = index.CreateFeature("Login", "Sign in flow").Id;

        using var reopened = new CodaleDatabase(Folder);
        using var again = new ProjectKnowledgeIndex(reopened);
        Assert.Equal("Login", again.GetFeature(id)!.Name);
        Assert.Equal(id, again.SearchFeatures("login", 3).Single().Id);
    }

    [Fact]
    public void Search_finds_the_feature_by_its_name_summary_and_prompts()
    {
        using var db = new CodaleDatabase(Folder);
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
        using var db = new CodaleDatabase(Folder);
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
        using var db = new CodaleDatabase(Folder);
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
        using var db = new CodaleDatabase(Folder);
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
        using var db = new CodaleDatabase(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var f = index.CreateFeature("Auth", "");
        index.RenameFeature(f.Id, "Authentication");

        Assert.Equal(f.Id, index.SearchFeatures("authentication", 3).Single().Id);
    }

    [Fact]
    public void Files_recorded_for_a_session_that_is_not_a_feature_do_not_appear_in_the_history()
    {
        using var db = new CodaleDatabase(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        index.RecordSessionFile("global-search", "src/Auth.cs");

        Assert.Empty(index.ListFeatures());
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void Explicit_provider_mapping_survives_restart_and_reconfiguration(string provider)
    {
        string sessionId;
        using (var db = new CodaleDatabase(Folder))
        using (var index = new ProjectKnowledgeIndex(db))
        {
            sessionId = index.CreateFeature("First prompt", "").Id;
            index.SetProviderSession(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"));
        }

        using var reopenedDb = new CodaleDatabase(Folder);
        using var reopened = new ProjectKnowledgeIndex(reopenedDb);
        Assert.Equal(new ProviderSession(sessionId, provider, "native", "checkout", "endpoint"), reopened.GetProviderSession(sessionId));
        reopened.SetProviderSession(new ProviderSession(sessionId, provider, "replacement", "checkout", "endpoint2"));
        Assert.Equal("replacement", reopened.GetProviderSession(sessionId)!.ProviderSessionId);
        Assert.Single(reopened.ListFeatures());
    }

    [Fact]
    public void Deleting_a_session_removes_its_provider_mapping()
    {
        using var db = new CodaleDatabase(Folder);
        using var index = new ProjectKnowledgeIndex(db);
        var session = index.CreateFeature("Session", "");
        index.SetProviderSession(new ProviderSession(session.Id, "codex", "native", "checkout", ""));

        index.DeleteFeature(session.Id);

        Assert.Null(index.GetProviderSession(session.Id));
        Assert.Empty(index.ListFeatures());
    }
}