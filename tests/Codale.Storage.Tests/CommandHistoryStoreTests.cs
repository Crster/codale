namespace Codale.Storage.Tests;

public sealed class CommandHistoryStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-cmd-").FullName;
    private readonly CodaleStore _store;

    private const string ProjectA = @"X:\Projects\Alpha";
    private const string ProjectB = @"X:\Projects\Beta";

    public CommandHistoryStoreTests() => _store = new CodaleStore(Path.Combine(_directory, "codale.db"));

    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void A_repeated_command_is_kept_once_and_moves_to_the_front()
    {
        _store.RecordCommand(ProjectA, "git status", "shell", At(1));
        _store.RecordCommand(ProjectA, "dotnet build", "terminal", At(2));
        _store.RecordCommand(ProjectA, "git status", "terminal", At(3));

        Assert.Equal(["git status", "dotnet build"], _store.RecentCommands(ProjectA));
    }

    [Fact]
    public void Suggestions_match_the_typed_start_and_put_the_most_used_first()
    {
        _store.RecordCommand(ProjectA, "git status", "shell", At(1));
        _store.RecordCommand(ProjectA, "git commit -m x", "shell", At(2));
        _store.RecordCommand(ProjectA, "git commit -m x", "shell", At(3));
        _store.RecordCommand(ProjectA, "npm test", "shell", At(4));

        Assert.Equal(["git commit -m x", "git status"], _store.CommandsStartingWith(ProjectA, "git "));
        Assert.Empty(_store.CommandsStartingWith(ProjectA, "git status"));
    }

    [Fact]
    public void A_typed_percent_or_underscore_is_matched_literally()
    {
        _store.RecordCommand(ProjectA, "a_b c", "shell", At(1));
        _store.RecordCommand(ProjectA, "axb c", "shell", At(2));
        _store.RecordCommand(ProjectA, "50% sale", "shell", At(3));
        _store.RecordCommand(ProjectA, "500 sale", "shell", At(4));

        Assert.Equal(["a_b c"], _store.CommandsStartingWith(ProjectA, "a_"));
        Assert.Equal(["50% sale"], _store.CommandsStartingWith(ProjectA, "50%"));
    }

    [Fact]
    public void Each_project_has_its_own_history()
    {
        _store.RecordCommand(ProjectA, "cargo test", "shell", At(1));

        Assert.Empty(_store.RecentCommands(ProjectB));
        Assert.Empty(_store.CommandsStartingWith(ProjectB, "cargo"));
    }

    [Fact]
    public void The_least_recently_used_commands_are_dropped_past_the_limit()
    {
        for (var i = 0; i < CodaleStore.MaxCommandsPerProject + 5; i++)
        {
            _store.RecordCommand(ProjectA, $"cmd {i}", "shell", At(i + 1));
        }

        var kept = _store.RecentCommands(ProjectA, max: int.MaxValue);
        Assert.Equal(CodaleStore.MaxCommandsPerProject, kept.Count);
        Assert.Equal($"cmd {CodaleStore.MaxCommandsPerProject + 4}", kept[0]);
        Assert.DoesNotContain("cmd 0", kept);
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
