using Codale.Workspace;

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
    public void Config_replaces_a_command_of_the_same_name_and_reads_back_an_edited_file()
    {
        var config = new ConfigStore(_directory);
        config.AddCommand("test", "dotnet test", "src");
        config.AddCommand("TEST", "dotnet test --no-build");

        var saved = Assert.Single(config.Commands());
        Assert.Equal("dotnet test --no-build", saved.Command);

        var path = config.ExportForEditing();
        File.WriteAllText(path, "{ \"commands\": [ { \"name\": \"lint\", \"command\": \"npm run lint\" } ] }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        config.SyncEdited();

        Assert.Equal(["lint"], config.Commands().Select(c => c.Name));
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
        Assert.ThrowsAny<ArgumentException>(() => { env.Set("bad name", "x"); });

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
