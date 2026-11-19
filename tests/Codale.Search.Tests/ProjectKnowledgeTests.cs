using Codale.Db;

namespace Codale.Search.Tests;

public sealed class ProjectKnowledgeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-knowledge-tests-").FullName;
    private readonly CodaleDb _store;
    private readonly ProjectKnowledgeIndex _index;
    private string Root => Path.Combine(_directory, "project");
    public ProjectKnowledgeTests()
    {
        Directory.CreateDirectory(Root);
        _store = new CodaleDb(Path.Combine(_directory, "project.db"));
        _index = new ProjectKnowledgeIndex(_store);
    }
    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private string[] Found(string query) => [.. _index.Search(Root, query, maxFiles: 8).Select(c => c.Path.Replace('\\', '/')).Distinct()];

    [Fact]
    public async Task Shell_edits_are_recorded_as_session_changes_but_branch_switches_are_not()
    {
        Write(".git/HEAD", "ref: refs/heads/main");
        Write(".git/refs/heads/main", "1111111");
        Write("rivals.go", "package game\nconst Distance = 1");
        Write("timing.go", "package game\nconst Minutes = 30");
        var before = DateTime.UtcNow;
        await _index.RefreshAsync(Root);
        Assert.Empty(_index.ChangedSince(Root, before));   // the first index is not session work

        Write("timing.go", "package game\nconst Minutes = 50");  // e.g. sed -i
        Write(".git/refs/heads/main", "2222222");                 // ...then committed
        await _index.RefreshAsync(Root);
        Assert.Equal(["timing.go"], _index.ChangedSince(Root, before));

        var switched = DateTime.UtcNow;
        Write(".git/HEAD", "ref: refs/heads/other");
        Write(".git/refs/heads/other", "3333333");
        Write("rivals.go", "package game\nconst Distance = 2");
        await _index.RefreshAsync(Root);
        Assert.Empty(_index.ChangedSince(Root, switched));
    }

    [Theory]
    [InlineData("ref: refs/heads/main aaa", "ref: refs/heads/main bbb", true)]
    [InlineData("ref: refs/heads/main aaa", "ref: refs/heads/other aaa", false)]
    [InlineData("aaa", "bbb", false)]
    [InlineData("unknown", "unknown", true)]
    public void Same_branch_ignores_commits_but_not_switches(string previous, string current, bool same) =>
        Assert.Equal(same, ProjectKnowledgeIndex.SameBranch(previous, current));

    [Fact]
    public async Task Changed_and_deleted_source_never_returns_stale_excerpts()
    {
        Write("Auth.cs", "class Authentication { void OldRefreshToken() {} }");
        await _index.RefreshAsync(Root);
        Write("Auth.cs", "class Authentication { void NewCredentialExchange() {} }");
        Assert.DoesNotContain(_index.Search(Root, "OldRefreshToken"), c => c.Text.Contains("OldRefreshToken"));
        await _index.RefreshAsync(Root);
        Assert.Contains(_index.Search(Root, "NewCredentialExchange"), c => c.Text.Contains("NewCredentialExchange"));
        File.Delete(Path.Combine(Root, "Auth.cs"));
        Assert.Empty(_index.Search(Root, "NewCredentialExchange"));
    }

    [Fact]
    public async Task Index_excludes_secrets_binary_generated_ignored_and_database_files()
    {
        Write("Auth.cs", "class EligibleAuthentication { string credential; }");
        Write(".env.local", "SECRET=credential");
        Write("secrets.json", "credential");
        Write("node_modules/dep/index.js", "credential");
        Write("obj/generated.cs", "credential");
        Write("local.db", "credential");
        Write("binary.cs", "credential\0binary");
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs", "credential");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        await _index.RefreshAsync(Root);
        Assert.Equal(["Auth.cs"], Found("credential"));
    }

    [Fact]
    public void Source_paths_cannot_escape_the_checkout()
    {
        File.WriteAllText(Path.Combine(_directory, "outside.cs"), "sensitive source");
        Assert.Null(ProjectKnowledgeIndex.ReadSource(Root, "../outside.cs"));
        Assert.Null(ProjectKnowledgeIndex.ReadSource(Root, Path.Combine(_directory, "outside.cs")));
    }

    [Fact]
    public async Task Code_outranks_design_notes_that_repeat_its_words()
    {
        Write("docs/DESIGN.md", "# Garrison\nThe garrison panel lets the player station servants. Garrison garrison garrison.");
        Write("client/garrison_view.js", "export function openGarrison() { renderGarrison(); }");
        await _index.RefreshAsync(Root);
        Assert.Equal("client/garrison_view.js", Found("garrison panel")[0]);
    }

    public void Dispose()
    {
        _index.Dispose();
        _store.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
