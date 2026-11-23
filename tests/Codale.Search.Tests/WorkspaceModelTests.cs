using Codale.Search.Engine;
using Codale.Db;
using System.Text;

namespace Codale.Search.Tests;

public sealed class WorkspaceModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-model-").FullName;

    private static readonly DateTime Written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A file as the scan builds it, with its text retained.</summary>
    private static SourceFile Make(string relative, string text, DateTime? written = null)
    {
        var (keys, counts, total) = SourceTokens.Count(text);
        return new SourceFile(
            relative,
            Path.Combine(@"C:\root", relative),
            text.Length,
            written ?? Written,
            text,
            keys,
            counts,
            total,
            SourceSymbols.Extract(text, path: relative),
            SourceTokens.Transitions(text));
    }

    private static List<(string Text, SourceSymbolKind? Kind, int Files)> Show(WorkspaceModel model, string prefix, string? previous = null) =>
        [.. model.Suggest(prefix, previous).Select(s => (s.Text, s.Kind, s.Files))];

    [Fact]
    public void Replacing_or_removing_a_file_leaves_the_same_model_as_building_it_fresh()
    {
        var incremental = new WorkspaceModel();
        incremental.Put(Make("a.cs", "class Alpha { void loadUser() {} }"));
        incremental.Put(Make("b.cs", "var userName = loadUser();"));
        incremental.Put(Make("a.cs", "class Alpha { void saveOrder() {} }"));
        incremental.Remove("b.cs");

        var fresh = new WorkspaceModel();
        fresh.Put(Make("a.cs", "class Alpha { void saveOrder() {} }"));

        Assert.Equal(Show(fresh, "s"), Show(incremental, "s"));
        Assert.Equal(Show(fresh, "a"), Show(incremental, "a"));
        Assert.Empty(incremental.Suggest("load"));
        Assert.Equal(1, incremental.FileCount);
    }

    [Theory]
    [InlineData("compile", "compile", 0)]
    [InlineData("compiel", "compile", 1)]
    [InlineData("compil", "compile", 1)]
    [InlineData("cmopile", "compile", 1)]
    [InlineData("cmpiel", "compile", 2)]
    [InlineData("build", "compile", 3)]
    public void Edit_distance_counts_a_swap_once_and_stops_past_its_limit(string a, string b, int expected)
    {
        Assert.Equal(expected, WorkspaceModel.EditDistance(a, b, 2));
    }

    [Fact]
    public void A_misspelled_word_is_corrected_to_the_workspace_spelling()
    {
        var model = new WorkspaceModel();
        model.Put(Make("Index.cs", "public class SourceIndex { } // compile the project"));
        model.Put(Make("use.cs", "var index = new SourceIndex(); // compile it"));

        Assert.True(model.Knows("compile"));
        Assert.True(model.Knows("SourceIndex"));
        Assert.False(model.Knows("compiel"));
        Assert.Equal("compile", model.Correct("compiel")[0].Text);

        var name = model.Correct("SoruceIndex")[0];
        Assert.Equal("SourceIndex", name.Text);
        Assert.Equal(SourceSymbolKind.Type, name.Kind);

        Assert.Empty(model.Correct("zebra"));
    }

    [Fact]
    public void Declared_names_rank_above_plain_identifiers_and_carry_their_kind()
    {
        var model = new WorkspaceModel();
        model.Put(Make("Retry.cs", "public class RetryPolicy { }"));
        model.Put(Make("use.cs", "retryPolicyName = retryPolicyName2;"));

        var suggestions = model.Suggest("retry");

        Assert.Equal("RetryPolicy", suggestions[0].Text);
        Assert.Equal(SourceSymbolKind.Type, suggestions[0].Kind);
    }

    [Fact]
    public void The_word_before_the_cursor_moves_what_followed_it_to_the_front()
    {
        var model = new WorkspaceModel();
        model.Put(Make("a.cs", "x loadOther; x loadOther; x loadOther; y loadOther;"));
        model.Put(Make("b.cs", "return loadFirst;"));

        Assert.Equal("loadFirst", model.Suggest("load", previous: "return")[0].Text);
    }

    [Fact]
    public void A_saved_model_is_reloaded_and_a_stored_file_is_reused_without_its_text()
    {
        var path = Path.Combine(_directory, "model");
        var first = new WorkspaceModel(path);
        first.Put(Make("a.cs", "class Alpha { void loadUser() {} }"));
        Assert.True(first.IsDirty);
        first.Save();
        Assert.False(first.IsDirty);

        var second = new WorkspaceModel(path);
        second.Load();

        Assert.Equal(1, second.FileCount);
        Assert.Equal(Show(first, "load"), Show(second, "load"));
        var reused = second.Rehydrate("a.cs", @"C:\root\a.cs", "class Alpha { void loadUser() {} }".Length, Written);
        Assert.NotNull(reused);
        Assert.False(reused.IsRetained);
        Assert.Null(second.Rehydrate("a.cs", @"C:\root\a.cs", 3, Written));
    }

    [Fact]
    public void Saving_one_changed_file_appends_only_its_record()
    {
        var path = Path.Combine(_directory, "incremental");
        var model = new WorkspaceModel(path);
        for (var i = 0; i < 100; i++) model.Put(Make($"file{i}.cs", $"class Initial{i} {{ }}"));
        model.Save();

        long RecordBytes() => Directory.EnumerateFiles(Path.Combine(path, "records"), "record_*.db").Sum(file => new FileInfo(file).Length);
        var baseline = RecordBytes();
        model.Put(Make("file0.cs", "class UpdatedName { }"));
        model.Remove("file1.cs");
        model.Save();

        Assert.InRange(RecordBytes() - baseline, 1, baseline / 10);
        var reopened = new WorkspaceModel(path);
        reopened.Load();
        Assert.Equal(99, reopened.FileCount);
        Assert.True(reopened.Knows("UpdatedName"));
        Assert.False(reopened.Knows("Initial1"));
    }

    [Fact]
    public void Legacy_snapshots_do_not_populate_the_typed_workspace_model()
    {
        var path = Path.Combine(_directory, "stale");
        using (var db = new CodaleDb(path))
        {
            const string json = """{"format":1,"files":[{"path":"a.cs","channels":{"terms":1,"symbols":5,"transitions":1}}]}""";
            db.Open("workspace_models").Open("model").WriteBlob(Encoding.UTF8.GetBytes(json));
        }

        var reloaded = new WorkspaceModel(path);
        reloaded.Load();

        Assert.Equal(0, reloaded.FileCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_restarted_index_reuses_unchanged_files_from_the_saved_model()
    {
        var root = Path.Combine(_directory, "project");
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(_directory, "workspace-model");
        var file = Path.Combine(root, "app.cs");
        File.WriteAllText(file, "class Alpha {}");
        var written = File.GetLastWriteTimeUtc(file);

        using (var first = new SourceIndex(root, new SourceIndexOptions { Watch = false, ModelPath = modelPath }))
        {
            await first.GetSnapshotAsync();
            Assert.Contains(first.Model.Suggest("Alp"), s => s.Text == "Alpha");
        }

        // Same length and write time, different content: a re-read would see Bravo, the model does not.
        File.WriteAllText(file, "class Bravo {}");
        File.SetLastWriteTimeUtc(file, written);

        using var second = new SourceIndex(root, new SourceIndexOptions { Watch = false, ModelPath = modelPath });
        var snapshot = await second.GetSnapshotAsync();

        var stored = snapshot.Find("app.cs")!;
        Assert.False(stored.IsRetained);
        Assert.Contains(stored.Symbols, s => s.Name == "Alpha");
        Assert.DoesNotContain(stored.Symbols, s => s.Name == "Bravo");
    }

    [Fact]
    public void Workspaces_sharing_a_database_keep_their_models_separate()
    {
        var path = Path.Combine(_directory, "shared");
        var first = new WorkspaceModel(path, "first");
        var second = new WorkspaceModel(path, "second");
        first.Put(Make("a.cs", "class Alpha {}"));
        second.Put(Make("b.cs", "class Bravo {}"));
        first.Save();
        second.Save();

        var loadedFirst = new WorkspaceModel(path, "first");
        var loadedSecond = new WorkspaceModel(path, "second");
        loadedFirst.Load();
        loadedSecond.Load();
        Assert.Contains(loadedFirst.Suggest("Alp"), s => s.Text == "Alpha");
        Assert.Empty(loadedFirst.Suggest("Bra"));
        Assert.Contains(loadedSecond.Suggest("Bra"), s => s.Text == "Bravo");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
