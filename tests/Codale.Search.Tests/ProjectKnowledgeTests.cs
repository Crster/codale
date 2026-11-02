using System.Text;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Storage;

namespace Codale.Search.Tests;

public sealed class ProjectKnowledgeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-knowledge-tests-").FullName;
    private readonly FeatureStore _store;
    private readonly ProjectKnowledgeIndex _index;
    private string Root => Path.Combine(_directory, "project");
    public ProjectKnowledgeTests()
    {
        Directory.CreateDirectory(Root);
        _store = new FeatureStore(Path.Combine(_directory, "project.db"));
        _index = new ProjectKnowledgeIndex(_store);
    }
    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private RetrievalResult Retrieve(string query, IReadOnlySet<string>? delivered = null, string? root = null) =>
        _index.Retrieve(root ?? Root, query, "", delivered ?? new HashSet<string>());

    [Fact]
    public async Task Every_prompt_retrieves_its_topic_without_changing_session_history()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        Write("Charts.cs", "class RevenueChart { void RenderGraph() {} }");
        var session = _store.Create("Auth", "fix auth");
        await _index.RefreshAsync(Root);
        Assert.Contains("Auth.cs", Retrieve("RefreshToken").Text);
        Assert.DoesNotContain("Charts.cs", Retrieve("RefreshToken").Text);
        Assert.Contains("Charts.cs", Retrieve("RevenueChart").Text);
        Assert.Equal(session.Id, Assert.Single(_store.List()).Id);
    }

    [Fact]
    public async Task Changed_and_deleted_source_never_retrieves_stale_excerpts()
    {
        Write("Auth.cs", "class Authentication { void OldRefreshToken() {} }");
        await _index.RefreshAsync(Root);
        Write("Auth.cs", "class Authentication { void NewCredentialExchange() {} }");
        Assert.DoesNotContain("OldRefreshToken", Retrieve("OldRefreshToken").Text);
        await _index.RefreshAsync(Root);
        Assert.Contains("NewCredentialExchange", Retrieve("NewCredentialExchange").Text);
        File.Delete(Path.Combine(Root, "Auth.cs"));
        Assert.Empty(Retrieve("NewCredentialExchange").Text);
        await _index.RefreshAsync(Root);
        Assert.Empty(_store.SourceHashes(Root));
    }

    [Fact]
    public async Task Delivered_context_is_not_repeated_and_new_content_can_be_injected()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        await _index.RefreshAsync(Root);
        var first = Retrieve("RefreshToken");
        Assert.NotEmpty(first.Keys);
        var delivered = first.Keys.ToHashSet();
        Assert.Empty(Retrieve("RefreshToken", delivered).Text);
        Write("Auth.cs", "class Authentication { void RefreshToken(bool force) {} }");
        await _index.RefreshAsync(Root);
        Assert.Contains("bool force", Retrieve("RefreshToken", delivered).Text);
    }

    [Fact]
    public async Task Providers_share_findings_with_reference_freshness_checks()
    {
        const string source = "class Authentication { void RefreshToken() {} }";
        Write("Auth.cs", source);
        var session = _store.Create("Codex findings", "Authentication");
        _store.PutKnowledge(new KnowledgeHit("finding", session.Id, "codex", Root, "rev1", "RefreshToken uses a rotating credential",
            [new SourceReference("Auth.cs", ProjectKnowledgeIndex.Hash(source))]));
        var result = Retrieve("rotating credential");
        Assert.Contains("provider codex", result.Text);
        Assert.Contains(session.Id, result.Text);
        Write("Auth.cs", "class ReplacementAuthentication {}");
        Assert.DoesNotContain("rotating credential", Retrieve("rotating credential").Text);
        await _index.RefreshAsync(Root);
    }

    [Fact]
    public async Task Worktrees_share_verified_findings_but_never_other_worktrees_source_chunks()
    {
        Write("Auth.cs", "class CheckoutOneAuthentication {}");
        var second = Path.Combine(_directory, "worktree2");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "Auth.cs"), "class CheckoutTwoAuthentication {}");
        await _index.RefreshAsync(Root);
        await _index.RefreshAsync(second);
        _store.PutKnowledge(new KnowledgeHit("one", "session", "claude", Root, "branch1", "CheckoutOneAuthentication finding",
            [new SourceReference("Auth.cs", ProjectKnowledgeIndex.Hash(File.ReadAllText(Path.Combine(Root, "Auth.cs"))))]));
        Assert.DoesNotContain("CheckoutOneAuthentication", Retrieve("CheckoutOneAuthentication", root: second).Text);
        File.Copy(Path.Combine(Root, "Auth.cs"), Path.Combine(second, "Auth.cs"), true);
        Assert.Contains("finding", Retrieve("CheckoutOneAuthentication", root: second).Text);
    }

    [Fact]
    public async Task Index_excludes_secrets_binary_generated_ignored_and_database_files()
    {
        Write("Auth.cs", "class EligibleAuthentication {}");
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
        var paths = _store.SourceHashes(Root).Keys;
        Assert.Contains("Auth.cs", paths);
        Assert.DoesNotContain(paths, p => p.Contains("secret") || p.Contains("node_modules") || p.Contains("obj") ||
            p is ".env.local" or "local.db" or "binary.cs" or "ignored.cs");
    }

    [Fact]
    public async Task Partial_index_is_available_and_context_with_envelope_stays_under_4000_tokens()
    {
        for (var i = 0; i < 30; i++) Write($"Authentication{i}.cs", $"class Authentication{i} {{ string Credential = \"{new string('x', 300)}\"; }}");
        Assert.True(Retrieve("Authentication").Tokens <= ProjectKnowledgeIndex.MaxContextBytes);
        await _index.RefreshAsync(Root);
        var result = Retrieve("Authentication Credential");
        Assert.NotEmpty(result.Keys);
        Assert.True(Encoding.UTF8.GetByteCount(SessionBriefing.ContextBlock(result.Text)) <= 4000);
    }

    [Fact]
    public void Source_paths_cannot_escape_the_checkout()
    {
        File.WriteAllText(Path.Combine(_directory, "outside.cs"), "sensitive source");
        Assert.Null(ProjectKnowledgeIndex.ReadSource(Root, "../outside.cs"));
        Assert.Null(ProjectKnowledgeIndex.ReadSource(Root, Path.Combine(_directory, "outside.cs")));
    }

    [Fact]
    public void Findings_already_in_the_current_provider_conversation_are_not_reinjected()
    {
        _store.PutKnowledge(new KnowledgeHit("finding", "current", "codex", Root, "revision", "authentication decisions", []));
        var context = _index.Retrieve(Root, "authentication", "", new HashSet<string>(), "current");
        Assert.DoesNotContain("authentication decisions", context.Text);
        Assert.Contains("authentication decisions", Retrieve("authentication").Text);
    }

    [Fact]
    public async Task Branch_changes_invalidate_even_files_with_identical_size_and_write_time()
    {
        Write(".git/HEAD", "branch-one");
        Write("Auth.cs", "class BranchOneAuthentication {}");
        await _index.RefreshAsync(Root);
        var written = File.GetLastWriteTimeUtc(Path.Combine(Root, "Auth.cs"));
        Write("Auth.cs", "class BranchTwoAuthentication {}");
        File.SetLastWriteTimeUtc(Path.Combine(Root, "Auth.cs"), written);
        Write(".git/HEAD", "branch-two");
        await _index.RefreshAsync(Root);
        Assert.Contains("BranchTwoAuthentication", Retrieve("BranchTwoAuthentication").Text);
        Assert.DoesNotContain("BranchOneAuthentication", Retrieve("BranchOneAuthentication").Text);
    }

    public void Dispose()
    {
        _index.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
