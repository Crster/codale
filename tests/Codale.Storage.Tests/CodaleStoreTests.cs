using System.Globalization;

namespace Codale.Storage.Tests;

public sealed class CodaleStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-store-").FullName;
    private readonly CodaleStore _store;

    private const string ProjectA = @"X:\Projects\Alpha";
    private const string ProjectB = @"X:\Projects\Beta";

    public CodaleStoreTests() => _store = new CodaleStore(Path.Combine(_directory, "codale.db"));

    private static SessionRecord Session(string id, string project, bool running = false) => new()
    {
        SessionId = id,
        ProjectKey = Codale.Core.Projects.ProjectPaths.InstanceKey(project),
        ProjectPath = project,
        StartedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
        IsRunning = running,
    };

    [Fact]
    public void A_session_round_trips()
    {
        _store.UpsertSession(Session("s1", ProjectA) with
        {
            Title = "Add retry handling",
            WorktreePath = @"X:\Projects\Alpha\.codale\worktrees\1a2b",
            BranchName = "codale/1a2b",
            CostUsd = 1.25m,
        });

        var session = Assert.Single(_store.GetSessions(ProjectA));

        Assert.Equal("s1", session.SessionId);
        Assert.Equal("Add retry handling", session.Title);
        Assert.Equal("codale/1a2b", session.BranchName);
        Assert.Equal(1.25m, session.CostUsd);
    }

    [Fact]
    public void Sessions_are_scoped_to_their_project()
    {
        _store.UpsertSession(Session("a1", ProjectA));
        _store.UpsertSession(Session("b1", ProjectB));

        Assert.Equal("a1", Assert.Single(_store.GetSessions(ProjectA)).SessionId);
        Assert.Equal("b1", Assert.Single(_store.GetSessions(ProjectB)).SessionId);
    }

    [Fact]
    public void Different_spellings_of_one_path_are_the_same_project()
    {
        _store.UpsertSession(Session("s1", @"X:\Projects\Alpha"));

        // Windows paths are case insensitive and a trailing slash means nothing.
        Assert.Single(_store.GetSessions(@"x:\projects\alpha\"));
    }

    [Fact]
    public void Upsert_updates_rather_than_duplicating()
    {
        _store.UpsertSession(Session("s1", ProjectA, running: true) with { CostUsd = 0.10m });
        _store.UpsertSession(Session("s1", ProjectA, running: false) with { CostUsd = 0.50m });

        var session = Assert.Single(_store.GetSessions(ProjectA));
        Assert.Equal(0.50m, session.CostUsd);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void An_update_without_a_title_does_not_erase_the_existing_one()
    {
        _store.UpsertSession(Session("s1", ProjectA) with { Title = "Named session" });

        // A later cost update carries no title; the stored one must survive.
        _store.UpsertSession(Session("s1", ProjectA) with { CostUsd = 2m });

        Assert.Equal("Named session", Assert.Single(_store.GetSessions(ProjectA)).Title);
    }

    [Fact]
    public void Sessions_come_back_newest_first()
    {
        _store.UpsertSession(Session("old", ProjectA) with { UpdatedAt = DateTimeOffset.Now.AddHours(-2) });
        _store.UpsertSession(Session("new", ProjectA) with { UpdatedAt = DateTimeOffset.Now });

        var sessions = _store.GetSessions(ProjectA);
        Assert.Equal("new", sessions[0].SessionId);
        Assert.Equal("old", sessions[1].SessionId);
    }

    [Fact]
    public void Sessions_left_running_are_what_crash_recovery_offers()
    {
        _store.UpsertSession(Session("crashed", ProjectA, running: true));
        _store.UpsertSession(Session("finished", ProjectA, running: false));

        var interrupted = Assert.Single(_store.GetInterruptedSessions(ProjectA));
        Assert.Equal("crashed", interrupted.SessionId);
    }

    [Fact]
    public void Marking_stopped_clears_the_recovery_offer()
    {
        _store.UpsertSession(Session("s1", ProjectA, running: true));
        _store.MarkStopped("s1");

        Assert.Empty(_store.GetInterruptedSessions(ProjectA));
    }

    [Fact]
    public void Mark_all_stopped_only_touches_the_one_project()
    {
        _store.UpsertSession(Session("a1", ProjectA, running: true));
        _store.UpsertSession(Session("b1", ProjectB, running: true));

        _store.MarkAllStopped(ProjectA);

        Assert.Empty(_store.GetInterruptedSessions(ProjectA));
        Assert.Single(_store.GetInterruptedSessions(ProjectB));
    }

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
            first.UpsertSession(Session("s1", ProjectA) with { Title = "Persisted" });
        }

        using var second = new CodaleStore(path);
        Assert.Equal("Persisted", Assert.Single(second.GetSessions(ProjectA)).Title);
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
    public void Timestamps_round_trip_whatever_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        var started = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.FromHours(8));

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            _store.UpsertSession(Session("s1", ProjectA) with { StartedAt = started, UpdatedAt = started });

            var session = Assert.Single(_store.GetSessions(ProjectA));
            Assert.Equal(started, session.StartedAt);
            Assert.Equal(started.Offset, session.StartedAt.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
