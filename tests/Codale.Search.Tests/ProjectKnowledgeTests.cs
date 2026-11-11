using System.Text;
using Codale.Core.Agents;
using Codale.Core.Features;
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
    private RetrievalResult Retrieve(string query, IReadOnlySet<string>? delivered = null, string? root = null) =>
        _index.Retrieve(root ?? Root, query, "", delivered ?? new HashSet<string>());

    [Fact]
    public void First_prompt_waits_briefly_for_the_initial_source_index()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        Assert.Contains("Auth.cs", Retrieve("RefreshToken").Files);
    }

    [Fact]
    public async Task Unknown_topic_does_not_send_unrelated_neighbours()
    {
        using var retrieval = new ProjectKnowledgeIndex(_store);
        Write("src/Authentication.cs", "class Authentication { bool RefreshCredential() => true; }");
        Write("src/RevenueChart.cs", "class RevenueChart { void DrawMonthlyGraph() {} }");
        await retrieval.RefreshAsync(Root);
        const string query = "zygomorphic quasar submarine teleportation";
        Assert.Empty(retrieval.Retrieve(Root, query, "", new HashSet<string>()).Text);
    }

    [Fact]
    public async Task Old_chunk_formats_are_rebuilt_even_when_source_hash_is_unchanged()
    {
        const string text = "\n\n    public void RefreshToken() {}\n";
        Write("Auth.cs", text);
        _index.ReplaceSource(Root, "Auth.cs", ProjectKnowledgeIndex.Hash(text), [(1, "public void RefreshToken() {}", "")]);
        await _index.RefreshAsync(Root);
        var chunk = Assert.Single(_index.SourceChunksForPath(Root, "Auth.cs"));
        Assert.Equal(3, chunk.StartLine);
        Assert.StartsWith("    public", chunk.Text);
        Assert.Equal(FileChunker.Version, _index.SourceChunkVersion(Root));
    }

    [Fact]
    public async Task Agent_edits_teach_future_prompts_which_file_to_open()
    {
        Write("src/component-a.tsx", "export const Panel = () => null;");
        Write("src/unrelated.tsx", "export const Other = () => null;");
        await _index.RefreshAsync(Root);
        Assert.Empty(_index.Search(Root, "projects owner picker"));

        _index.RecordLearnedEdit("session-1", Root, "src/component-a.tsx", "Improve projects owner picker");
        Assert.Equal("src/component-a.tsx", _index.Search(Root, "projects owner picker")[0].Path.Replace('\\', '/'));
        Assert.Contains("src/component-a.tsx", Retrieve("projects owner picker").Text);
        Assert.DoesNotContain(_index.Search(Root, "hiky permission"), c => c.Path.Replace('\\', '/') == "src/component-a.tsx");

        File.Delete(Path.Combine(Root, "src/component-a.tsx"));
        Assert.DoesNotContain(_index.Search(Root, "projects owner picker"), c => c.Path.Replace('\\', '/') == "src/component-a.tsx");
    }

    [Fact]
    public async Task Working_set_keeps_files_the_session_edited_in_view_for_follow_ups()
    {
        // The follow-up names neither the file nor anything in it, but other files repeat its words.
        Write("server/timing.go", "package game\nconst RaidArrivalMinutes = 30\nfunc RaidTravel(distance int) int { return RaidArrivalMinutes }");
        Write("server/terrain.go", "package game\n// Highland villages sit inland, away from the sea.\nfunc HighlandVillage() bool { return true }");
        Write("client/map_view.js", "// Draw highland villages and their minutes of daylight.\nexport function drawVillage() {}");
        for (var i = 0; i < 6; i++)
            Write($"client/view{i}.js", $"// Village panel {i}: highland minutes shown to the player.\nexport function village{i}() {{}}");
        await _index.RefreshAsync(Root);
        const string followUp = "if the village is in the highland add 20 more minutes";

        Assert.DoesNotContain("server/timing.go", _index.Retrieve(Root, followUp, "", new HashSet<string>()).Files);
        var boosted = _index.Retrieve(Root, followUp, "", new HashSet<string>(), workingSet: ["server/terrain.go", "server/timing.go"]);
        Assert.Contains("server/timing.go", boosted.Files);
        Assert.Contains("server/terrain.go", boosted.Files);
    }

    [Fact]
    public async Task Working_set_does_not_outrank_what_the_prompt_is_clearly_about()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} void RotateCredential() {} }");
        for (var i = 0; i < 8; i++) Write($"Old{i}.cs", $"class Unrelated{i} {{ void Paint{i}() {{}} }}");
        await _index.RefreshAsync(Root);
        var result = _index.Search(Root, "RefreshToken RotateCredential");
        var boosted = _index.Retrieve(Root, "RefreshToken RotateCredential", "", new HashSet<string>(),
            workingSet: Enumerable.Range(0, 8).Select(i => $"Old{i}.cs").ToList());
        Assert.Equal("Auth.cs", boosted.Files[0]);
        // Only the newest edits keep a seat on recency alone; the older ones need relevance.
        Assert.True(boosted.Files.Count(f => f.StartsWith("Old")) <= 2, string.Join(", ", boosted.Files));
        Assert.Equal("Auth.cs", result[0].Path);
    }

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
    public async Task Discovery_and_enrichment_share_fresh_hybrid_candidates()
    {
        using var retrieval = new ProjectKnowledgeIndex(_store);
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        Write("Charts.cs", "class RevenueChart { void RenderGraph() {} }");
        await retrieval.RefreshAsync(Root);
        foreach (var (query, expected) in new[] { ("where is RefreshToken implemented", "Auth.cs"), ("where is RevenueChart rendered", "Charts.cs") })
        {
            Assert.Equal(expected, (await retrieval.SearchAsync(Root, query))[0].Path);
            var explorer = new SourceExplorer(SourceIndex.For(Root), null) { Knowledge = retrieval, MaxRelated = 0 };
            var result = await explorer.RunAsync(query);
            Assert.Equal(expected, result.Files[0].RelativePath);
            Assert.Contains(expected, Retrieve(query).Text);
        }
        Write("Auth.cs", "class Authentication { void ExchangeCredential() {} }");
        Assert.DoesNotContain(retrieval.Search(Root, "RefreshToken"), c => c.Path == "Auth.cs");
    }

    [Fact]
    public async Task Every_prompt_retrieves_its_topic_without_changing_session_history()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        Write("Charts.cs", "class RevenueChart { void RenderGraph() {} }");
        var session = _index.CreateFeature("Auth", "fix auth");
        await _index.RefreshAsync(Root);
        Assert.Contains("Auth.cs", Retrieve("RefreshToken").Text);
        Assert.DoesNotContain("Charts.cs", Retrieve("RefreshToken").Text);
        Assert.Contains("Charts.cs", Retrieve("RevenueChart").Text);
        Assert.Equal(session.Id, Assert.Single(_index.ListFeatures()).Id);
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
        Assert.Empty(_index.SourceHashes(Root));
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
        var session = _index.CreateFeature("Codex findings", "Authentication");
        _index.PutKnowledge(new KnowledgeHit("finding", session.Id, "codex", Root, "rev1", "RefreshToken uses a rotating credential",
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
        _index.PutKnowledge(new KnowledgeHit("one", "session", "claude", Root, "branch1", "CheckoutOneAuthentication finding",
            [new SourceReference("Auth.cs", ProjectKnowledgeIndex.Hash(File.ReadAllText(Path.Combine(Root, "Auth.cs"))))]));
        Assert.DoesNotContain("CheckoutOneAuthentication", Retrieve("CheckoutOneAuthentication", root: second).Text);
        File.Copy(Path.Combine(Root, "Auth.cs"), Path.Combine(second, "Auth.cs"), true);
        Assert.Contains("earlier work", Retrieve("CheckoutOneAuthentication", root: second).Text);
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
        var paths = _index.SourceHashes(Root).Keys;
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
        _index.PutKnowledge(new KnowledgeHit("finding", "current", "codex", Root, "revision", "authentication decisions", []));
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

    [Fact]
    public async Task Natural_location_questions_find_the_file_and_definition_despite_inflections()
    {
        Write("db/connectors.ts", "export function createConnectorsTable(db) {\n  db.exec(`CREATE TABLE IF NOT EXISTS connectors (id TEXT PRIMARY KEY, name TEXT)`);\n}\n");
        Write("db/orders.ts", "export function createOrdersTable(db) {\n  db.exec(`CREATE TABLE IF NOT EXISTS orders (id TEXT)`);\n}\n");
        Write("README.md", "This project does new things. Where things are located is documented elsewhere.");
        await _index.RefreshAsync(Root);
        var result = Retrieve("where does the new connector table located?");
        Assert.Equal("db/connectors.ts", result.Files.First());
        Assert.Contains("createConnectorsTable (L1)", result.Text);
        Assert.Contains("connectors (L2)", result.Text);
        Assert.DoesNotContain("README.md", result.Files);
        Assert.Equal("BM25 + n-gram vectors", result.Method);
    }

    [Fact]
    public async Task One_large_file_cannot_crowd_out_other_relevant_files()
    {
        Write("Connector.cs", string.Join("\n", Enumerable.Range(0, 400).Select(i => $"void Connector{i}() {{ /* connector */ }}")));
        Write("ConnectorDrawer.tsx", "export function ConnectorDrawer() { return null; }");
        await _index.RefreshAsync(Root);
        var result = Retrieve("connector");
        Assert.Contains("ConnectorDrawer.tsx", result.Files);
        Assert.InRange(result.Keys.Count(k => k.StartsWith("source:Connector.cs:", StringComparison.Ordinal)), 1, 2);
        Assert.Contains(result.Keys, k => k.StartsWith("source:ConnectorDrawer.tsx:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Vector_leg_matches_identifier_fragments_lexical_search_misses()
    {
        Write("Billing.cs", "class InvoiceReconciliationWorker { void ReconcileInvoices() {} }");
        Write("Other.cs", "class RevenueChart { void RenderGraph() {} }");
        await _index.RefreshAsync(Root);
        // "reconcile" is not a whole token in the file (only "reconciliation", "reconcileinvoices").
        Assert.Contains("Billing.cs", Retrieve("reconcile").Files);
    }

    [Fact]
    public async Task File_map_entries_are_not_repeated_once_delivered()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        await _index.RefreshAsync(Root);
        var first = Retrieve("RefreshToken");
        Assert.Contains(first.Keys, k => k.StartsWith("map:Auth.cs:" + ProjectKnowledgeIndex.Hash(File.ReadAllText(Path.Combine(Root, "Auth.cs"))) + ":", StringComparison.Ordinal));
        var second = Retrieve("RefreshToken", first.Keys.ToHashSet());
        Assert.Empty(second.Files);
        Assert.Empty(second.Text);
    }

    [Fact]
    public async Task Enrichment_keeps_two_ranked_chunks_while_discovery_keeps_one_per_file()
    {
        Write("Auth.cs", string.Join("\n", Enumerable.Range(0, 70).Select(i =>
            i == 0 ? "class Authentication { void RefreshToken() {} }" :
            i == 55 ? "void RefreshTokenFallback() { RotateCredential(); }" : "// authentication")));
        await _index.RefreshAsync(Root);
        Assert.Single(_index.Search(Root, "RefreshToken"));
        var result = Retrieve("RefreshToken");
        Assert.Equal(2, result.Keys.Count(k => k.StartsWith("source:Auth.cs:", StringComparison.Ordinal)));
        Assert.Contains("RotateCredential", result.Text);
    }

    [Fact]
    public async Task Changing_topic_in_an_unchanged_file_delivers_the_new_definition_location()
    {
        Write("Features.cs", "class Features {}\nvoid Placeholder() {}\npublic void Login() {}\npublic void Logout() {}\npublic void RefreshToken() {}\npublic void RevenueChart() {}");
        await _index.RefreshAsync(Root);
        var first = Retrieve("RefreshToken");
        var next = Retrieve("RevenueChart", first.Keys.ToHashSet());
        Assert.Contains("RevenueChart (L6)", next.Text);
        Assert.Contains("Features.cs", next.Files);
        Assert.DoesNotContain(next.Keys, k => k.StartsWith("source:", StringComparison.Ordinal));
        Assert.Empty(Retrieve("RevenueChart", first.Keys.Concat(next.Keys).ToHashSet()).Text);
    }

    [Theory]
    [InlineData(LearnedKind.Edit)]
    [InlineData(LearnedKind.Open)]
    [InlineData(LearnedKind.Read)]
    public async Task Every_kind_of_learned_association_lifts_its_file_for_a_similar_prompt(string kind)
    {
        Write("src/component-a.tsx", "export const Panel = () => null;");
        Write("src/unrelated.tsx", "export const Other = () => null;");
        await _index.RefreshAsync(Root);
        Assert.Empty(_index.Search(Root, "projects owner picker"));

        _index.RecordLearned("s", Root, "src/component-a.tsx", "show the projects owner picker", kind);
        Assert.Equal("src/component-a.tsx", _index.Search(Root, "projects owner picker")[0].Path.Replace('\\', '/'));
    }

    [Fact]
    public async Task An_opened_search_result_outranks_a_file_that_was_only_read_and_an_edit_outranks_both()
    {
        Write("a.tsx", "export const A = 1;");
        Write("b.tsx", "export const B = 1;");
        Write("c.tsx", "export const C = 1;");
        await _index.RefreshAsync(Root);
        _index.RecordLearned("s", Root, "a.tsx", "rotate the owner picker", LearnedKind.Read);
        _index.RecordLearned("s", Root, "b.tsx", "rotate the owner picker", LearnedKind.Open);
        _index.RecordLearned("s", Root, "c.tsx", "rotate the owner picker", LearnedKind.Edit);
        Assert.Equal(["c.tsx", "b.tsx", "a.tsx"], _index.Search(Root, "rotate owner picker").Select(c => c.Path).ToArray());
    }

    [Fact]
    public async Task A_named_identifier_hands_over_its_line_and_the_code_around_it()
    {
        var filler = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"// filler {i} about files and defaults"));
        Write("Index.cs", "class Index\n{\n    private const int MaxFiles = 6;\n}\n" + filler + "\nvoid Search() { var files = 1; }");
        await _index.RefreshAsync(Root);
        var result = Retrieve("Change the default MaxFiles from 6 to 5");
        Assert.Contains("Matches (path:line):", result.Text);
        Assert.Contains("- Index.cs:3  private const int MaxFiles = 6;", result.Text);
        Assert.Contains("Index.cs:1-", result.Text);
        Assert.Contains("private const int MaxFiles = 6;", result.Text);
        Assert.DoesNotContain("const (", result.Text);
        Assert.Equal(new[] { "MaxFiles", "rank_ids", "RankAsync" }, ProjectKnowledgeIndex.Identifiers("Change MaxFiles and rank_ids in RankAsync, not the plain words"));
        Assert.Empty(ProjectKnowledgeIndex.Identifiers("Change the default value of files"));
    }

    [Fact]
    public async Task An_oversized_file_map_keeps_entries_that_fit()
    {
        for (var i = 0; i < 6; i++)
            Write($"{new string('a', 80)}{i}/Authentication.cs", "class Authentication {}\npublic void RefreshToken() {}\npublic void RotateCredential() {}\npublic void ValidateCredential() {}");
        await _index.RefreshAsync(Root);
        var result = Retrieve("Authentication");
        Assert.NotEmpty(result.Files);
        Assert.True(result.Files.Count < 6);
        Assert.True(result.Tokens <= ProjectKnowledgeIndex.MaxContextBytes);
    }

    [Fact]
    public async Task Source_excerpt_preserves_the_end_of_the_ranked_chunk()
    {
        Write("Auth.cs", "class Authentication {\n" + new string(' ', 1100) + "\npublic void RefreshToken() { RotateCredential(); }\n}");
        await _index.RefreshAsync(Root);
        var result = Retrieve("RefreshToken");
        Assert.Contains("RotateCredential();", result.Text);
    }

    [Fact]
    public void Go_receiver_methods_are_located_by_the_current_topic()
    {
        var source = "package game\nfunc (g *Game) Draw() {}\nfunc (g *Game) Update() {}\nfunc (g *Game) Layout(w, h int) (int, int) { return w, h }\nfunc (g *Game) HandleGarrison() {}\nfunc (g *Game) HandleClick() {}";
        Assert.Contains("HandleClick (L6)", ProjectKnowledgeIndex.Definitions(source, ["click"]));
    }

    [Fact]
    public async Task Distinctive_topic_words_outrank_generic_path_matches()
    {
        Write("server/game/units.cs", "class Unit { void Game() {} }");
        Write("client/defence.cs", "class Defence { void RenderMinimap() {} }");
        for (var i = 0; i < 8; i++) Write($"other/Feature{i}.cs", $"class Feature{i} {{ void GameUnit() {{}} }}");
        await _index.RefreshAsync(Root);
        Assert.Equal("client/defence.cs", Retrieve("minimap game unit").Files.First());
    }

    [Fact]
    public async Task Code_outranks_design_notes_and_instruction_files_are_never_repeated()
    {
        Write("docs/DESIGN.md", "# Garrison\nThe garrison panel lets the player station servants. Garrison garrison garrison.");
        Write("CLAUDE.md", "Garrison rules: the garrison view lives in client.");
        Write("client/garrison_view.js", "export function openGarrison() { renderGarrison(); }");
        await _index.RefreshAsync(Root);
        var result = Retrieve("garrison panel");
        Assert.Equal("client/garrison_view.js", result.Files[0]);
        Assert.DoesNotContain("CLAUDE.md", result.Files);
        Assert.Equal("docs/DESIGN.md", Retrieve("update the garrison docs").Files[0]);
    }

    [Fact]
    public async Task A_file_edited_mid_turn_is_still_retrieved()
    {
        Write("Auth.cs", "class Authentication { void RefreshToken() {} }");
        await _index.RefreshAsync(Root);
        // The agent edits the file; a steer about it arrives before the index catches up.
        Write("Auth.cs", "class Authentication { void RefreshToken() { RotateCredential(); } }");
        var result = Retrieve("RefreshToken");
        Assert.Contains("Auth.cs", result.Files);
        Assert.Contains("RotateCredential", result.Text);
    }

    public void Dispose()
    {
        _index.Dispose();
        _store.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
