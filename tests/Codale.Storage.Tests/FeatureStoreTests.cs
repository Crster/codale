namespace Codale.Storage.Tests;

public sealed class FeatureStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-features-").FullName;
    private readonly FeatureStore _store;

    public FeatureStoreTests() => _store = new FeatureStore(Path.Combine(_directory, "features.db"));

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_feature_round_trips_and_lists_newest_first()
    {
        var login = _store.Create("Login", "Sign in flow");
        Thread.Sleep(5);
        var dashboard = _store.Create("Dashboard", "Charts");

        Assert.Equal([dashboard.Id, login.Id], _store.List().Select(f => f.Id));
        Assert.Equal("Sign in flow", _store.Get(login.Id)!.Description);
    }

    [Fact]
    public void Search_finds_the_feature_by_its_name_summary_and_prompts()
    {
        var login = _store.Create("Login", "");
        var dashboard = _store.Create("Dashboard", "");
        _store.SetSummary(login.Id, "Uses refresh tokens stored in the keychain", 10);
        _store.RecordProgress(dashboard.Id, 100, "redesign the revenue chart layout");

        Assert.Equal(login.Id, _store.Search("the refresh token expires too early", 3).First().Id);
        Assert.Equal(dashboard.Id, _store.Search("chart layout looks off", 3).First().Id);
        Assert.Empty(_store.Search("zzz qqq", 3));
    }

    [Fact]
    public void Summary_and_progress_are_kept()
    {
        var f = _store.Create("Login", "");
        _store.RecordProgress(f.Id, 500, "fix redirect");
        _store.SetSummary(f.Id, "## Done\nx", logOffset: 400);

        var read = _store.Get(f.Id)!;
        Assert.Equal(500, read.LogBytes);
        Assert.Equal(400, read.SummaryOffset);
        Assert.Equal("## Done\nx", read.Summary);

        _store.SetSummary(f.Id, "## Done\ny", logOffset: 0, logBytes: 0);
        Assert.Equal(0, _store.Get(f.Id)!.LogBytes);
    }

    [Fact]
    public void Written_files_are_searched_in_their_feature_first()
    {
        var login = _store.Create("Login", "");
        var dashboard = _store.Create("Dashboard", "");
        _store.ReplaceFile(login.Id, "src/Auth.cs", [(1, "class AuthService { void RefreshToken() {} }")]);
        _store.ReplaceFile(dashboard.Id, "src/Charts.cs", [(1, "class RevenueChart { void Render() {} }")]);

        var inLogin = _store.SearchFiles("refresh the token", login.Id, 5);
        var everywhere = _store.SearchFiles("revenue chart", null, 5);

        Assert.Equal("src/Auth.cs", Assert.Single(inLogin).Path);
        Assert.Equal("src/Charts.cs", Assert.Single(everywhere).Path);
        Assert.Empty(_store.SearchFiles("revenue chart", login.Id, 5));
        Assert.Equal(["src/Auth.cs"], _store.FilesOf(login.Id, 10));
        Assert.Equal(1, _store.FileCount(login.Id));
    }

    [Fact]
    public void Rewriting_a_file_replaces_what_was_indexed_of_it()
    {
        var f = _store.Create("Login", "");
        _store.ReplaceFile(f.Id, "src/Auth.cs", [(1, "old marker alpha")]);
        _store.ReplaceFile(f.Id, "src/Auth.cs", [(1, "new marker beta")]);

        Assert.Empty(_store.SearchFiles("alpha", f.Id, 5));
        Assert.Single(_store.SearchFiles("beta", f.Id, 5));
        Assert.Equal(1, _store.FileCount(f.Id));
    }

    [Fact]
    public void Deleting_a_feature_removes_its_files_and_search_entries()
    {
        var f = _store.Create("Login", "");
        _store.ReplaceFile(f.Id, "src/Auth.cs", [(1, "refresh token logic")]);

        _store.Delete(f.Id);

        Assert.Null(_store.Get(f.Id));
        Assert.Empty(_store.SearchFiles("refresh", null, 5));
        Assert.Empty(_store.Search("login", 5));
    }

    [Fact]
    public void Renaming_updates_what_search_finds()
    {
        var f = _store.Create("Auth", "");
        _store.Rename(f.Id, "Authentication");

        Assert.Equal(f.Id, _store.Search("authentication", 3).Single().Id);
    }
}
