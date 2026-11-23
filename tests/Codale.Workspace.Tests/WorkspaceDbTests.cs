namespace Codale.Workspace.Tests;

public sealed class WorkspaceDbTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codale-workspace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // The database lets go a moment after the last use.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void Legacy_workspace_folder_moves_under_the_data_root_once()
    {
        var legacy = Path.Combine(_directory, "workspace");
        var root = Path.Combine(_directory, "Codale", "workspace");
        Directory.CreateDirectory(Path.Combine(legacy, "my_repo"));

        WorkspaceDb.MoveLegacyRoot(legacy, root);
        Assert.False(Directory.Exists(legacy));
        Assert.True(Directory.Exists(Path.Combine(root, "my_repo")));

        // Once the new folder exists a reappearing old one is left alone.
        Directory.CreateDirectory(Path.Combine(legacy, "other"));
        WorkspaceDb.MoveLegacyRoot(legacy, root);
        Assert.True(Directory.Exists(Path.Combine(legacy, "other")));
        Assert.False(Directory.Exists(Path.Combine(root, "other")));
    }

    [Fact]
    public void Linked_worktrees_share_a_flat_database_and_same_named_projects_stay_separate()
    {
        var main = Path.Combine(_directory, "MyRepo");
        var worktree = Path.Combine(_directory, "checkout");
        var other = Path.Combine(_directory, "another", "MyRepo");
        var common = Path.Combine(main, ".git");
        var linked = Path.Combine(common, "worktrees", "checkout");
        Directory.CreateDirectory(linked);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + linked);
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..");

        var root = Path.Combine(_directory, "workspace");
        using var factory = new WorkspaceDataFactory(project => WorkspaceDb.DirectoryFor(project, root));
        var data = factory.Open(main);
        var checkout = factory.Open(worktree);
        var unrelated = factory.Open(other);
        Assert.Equal(Path.Combine(root, "my_repo"), data.Directory);
        Assert.Equal(data.Directory, checkout.Directory);
        Assert.Equal(Path.Combine(root, "my_repo_2"), unrelated.Directory);
        data.UseRecords(db => checkout.UseRecords(checkoutDb => Assert.Same(db, checkoutDb)));

        data.Config.Set("theme", "dark");
        data.History.Append("main-session", "main transcript");
        checkout.History.Append("checkout-session", "checkout transcript");
        Assert.Equal("dark", checkout.Config.Get("theme"));
        Assert.Equal(["main transcript"], checkout.History.Lines("main-session", HistoryStore.Current));
        Assert.Equal(["checkout transcript"], data.History.Lines("checkout-session", HistoryStore.Current));
        Assert.Empty(unrelated.History.Lines("main-session", HistoryStore.Current));
        Assert.True(File.Exists(Path.Combine(data.Directory, "index__records.db")));
        Assert.Empty(Directory.EnumerateDirectories(data.Directory));
    }

    [Fact]
    public void History_keeps_byte_offsets_and_archives_what_a_summary_read()
    {
        var history = new HistoryStore(_directory);
        history.Append("f", "{\"a\":1}");
        var summarised = history.Length("f");
        history.Append("f", "{\"b\":2}");

        history.ArchiveUpTo("f", summarised);

        Assert.Equal(1, history.Generation("f"));
        Assert.Equal(["arc.1", "log"], history.Generations("f"));
        Assert.Equal(["{\"a\":1}"], history.Lines("f", "arc.1"));
        Assert.Equal(["{\"b\":2}"], history.Lines("f", "log"));
        Assert.Equal(Text.Length("{\"b\":2}"), history.Length("f"));
    }

    [Fact]
    public void History_reads_from_an_offset_and_forgets_deleted_histories()
    {
        var history = new HistoryStore(_directory);
        history.Append("f", "one");
        var after = history.Length("f");
        history.Append("f", "two");

        Assert.Equal(["two"], history.Lines("f", HistoryStore.Current, after));

        history.Delete("f");
        history.Append("f", "late");
        Assert.Empty(history.Generations("f"));
    }

    [Fact]
    public void Workspace_history_pages_keep_utf8_offsets_across_reopen()
    {
        using (var pool = new WorkspaceDatabasePool())
        {
            var history = new HistoryStore(_directory, pool);
            history.Append("f", "archived");
            history.ArchiveUpTo("f", history.Length("f"));
            history.Append("f", "猫");
            history.Append("f", "next");
            Assert.Equal(["arc.1", "log"], history.Generations("f"));
            Assert.Equal([(4L, "猫"), (9L, "next")], history.Page("f", HistoryStore.Current, 0, 2));
            Assert.Empty(history.Page("f", HistoryStore.Current, 1, 2));
            history.ArchiveUpTo("f", 4);
            Assert.Equal(["猫"], history.Lines("f", "arc.2"));
            Assert.Equal([(5L, "next")], history.Page("f", HistoryStore.Current, 0, 2));
        }
        using var reopened = new WorkspaceDatabasePool();
        Assert.Equal(["archived"], new HistoryStore(_directory, reopened).Lines("f", "arc.1"));
    }

    [Fact]
    public void Memory_replaces_a_note_by_title_and_recalls_it()
    {
        var memory = new MemoryStore(_directory);
        memory.Save("Build command", "Use dotnet build Codale.slnx from the repo root", ["build"]);
        memory.Save("build  command", "Use dotnet build -c Release");
        memory.Save("Database", "Workspace data lives in the Codale data folder, not the repo");

        Assert.Equal(2, memory.List().Count);
        Assert.Equal("Use dotnet build -c Release", memory.Get("build command")!.Text);
        Assert.Equal("database", memory.Search("where does workspace data live").First().Id);

        Assert.True(memory.Forget("database"));
        Assert.Empty(memory.Search("data folder"));
    }

    [Fact]
    public void Config_replaces_a_command_of_the_same_name_and_renames_in_place()
    {
        var config = new ConfigStore(_directory);
        config.AddCommand("test", "dotnet test", "src");
        config.AddCommand("TEST", "dotnet test --no-build");

        var saved = Assert.Single(config.Commands());
        Assert.Equal("dotnet test --no-build", saved.Command);

        config.SaveCommand("test", new SavedCommand("lint", "npm run lint", Shell: "shell"));

        saved = Assert.Single(config.Commands());
        Assert.Equal("lint", saved.Name);
        Assert.Equal("shell", saved.Shell);

        Assert.True(config.RemoveCommand("LINT"));
        Assert.Empty(config.Commands());
        Assert.False(config.RemoveCommand("lint"));
    }

    [Fact]
    public void Env_is_encrypted_on_disk_and_lists_only_names()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var env = new EnvStore(_directory);
        env.Set("API_KEY", "s3cret-value-123");
        Assert.Equal("s3cret-value-123", env.Get("API_KEY"));
        Assert.Equal(["API_KEY"], env.Names());
        Assert.Equal(["TOKEN"], env.Import("# comment\nexport TOKEN=\"abc\"\nnot valid\n"));
        Assert.ThrowsAny<ArgumentException>(() => env.Set("bad name", "x"));

        // Wait for the database to let go, then look at the bytes: the secret must not be in them.
        Thread.Sleep(2000);
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("s3cret-value-123", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.True(env.Remove("API_KEY"));
        Assert.Null(env.Get("API_KEY"));
    }

    private static class Text
    {
        public static long Length(string line) => System.Text.Encoding.UTF8.GetByteCount(line) + 1;
    }
}
