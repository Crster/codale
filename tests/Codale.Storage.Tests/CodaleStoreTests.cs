namespace Codale.Storage.Tests;

public sealed class CodaleStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-store-").FullName;
    private readonly CodaleStore _store;

    private const string ProjectA = @"X:\Projects\Alpha";
    private const string ProjectB = @"X:\Projects\Beta";

    public CodaleStoreTests() => _store = new CodaleStore(Path.Combine(_directory, "codale.db"));

    [Fact]
    public void Todos_replace_the_previous_list_and_keep_their_order()
    {
        _store.SaveTodos("s1", [("first", "completed"), ("second", "in_progress")]);
        _store.SaveTodos("s1", [("only", "pending")]);

        var todos = _store.GetTodos("s1");

        Assert.Single(todos);
        Assert.Equal("only", todos[0].Content);
        Assert.Equal("pending", todos[0].Status);
    }

    [Fact]
    public void Todo_order_is_preserved()
    {
        _store.SaveTodos("s1", [("a", "pending"), ("b", "pending"), ("c", "pending")]);

        Assert.Equal(["a", "b", "c"], _store.GetTodos("s1").Select(t => t.Content));
    }

    [Fact]
    public void Ui_state_is_per_project()
    {
        _store.SetUiState(ProjectA, "left-width", "320");
        _store.SetUiState(ProjectB, "left-width", "180");

        Assert.Equal("320", _store.GetUiState(ProjectA, "left-width"));
        Assert.Equal("180", _store.GetUiState(ProjectB, "left-width"));
        Assert.Null(_store.GetUiState(ProjectA, "never-set"));
    }

    [Fact]
    public void The_database_survives_being_reopened()
    {
        var path = Path.Combine(_directory, "reopen.db");

        using (var first = new CodaleStore(path))
        {
            first.SetUiState(ProjectA, "left-width", "320");
        }

        using var second = new CodaleStore(path);
        Assert.Equal("320", second.GetUiState(ProjectA, "left-width"));
    }

    [Fact]
    public void A_database_from_a_newer_schema_is_refused()
    {
        var path = Path.Combine(_directory, "newer.db");

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version VALUES (999);";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<StoreSchemaException>(() => new CodaleStore(path));
        Assert.Contains("999", ex.Message);

        // The failed attempt is not remembered as migrated: it is refused again, not waved through.
        Assert.Throws<StoreSchemaException>(() => new CodaleStore(path));
    }

    [Fact]
    public void A_global_setting_round_trips_and_clears()
    {
        // The dotted keys are the agent endpoint settings; "" is how they clear.
        _store.SetSetting("endpoint.baseUrl", "https://gw.example/v1");

        Assert.Equal("https://gw.example/v1", _store.GetSetting("endpoint.baseUrl"));

        _store.SetSetting("endpoint.baseUrl", "");

        Assert.Equal("", _store.GetSetting("endpoint.baseUrl"));
        Assert.Null(_store.GetSetting("endpoint.never-set"));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
