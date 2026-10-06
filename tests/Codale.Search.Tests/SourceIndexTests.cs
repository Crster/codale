using System.Text.RegularExpressions;

namespace Codale.Search.Tests;

/// <summary>
/// The scan and its snapshot: what is indexed and what is left out, that a refresh
/// re-reads only what changed, and the in-memory grep and symbol table built from it.
/// </summary>
public sealed class SourceIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-index-").FullName;

    public SourceIndexTests()
    {
        Write("src/Uploader.cs", """
            public class Uploader
            {
                public void Upload()
                {
                    RetryPolicy.Execute(() => Send());
                }
            }
            """);
        Write("src/RetryPolicy.cs", """
            public static class RetryPolicy
            {
                public static void Execute(Action action) { }
            }
            """);
        Write("node_modules/lib/index.js", "function RetryPolicy() {}");
        Write("src/bin/Debug/Uploader.g.cs", "class RetryPolicy {}");
        Write(".env", "API_KEY=hunter2");
        Write("certs/server.pem", "-----BEGIN PRIVATE KEY-----");
        File.WriteAllBytes(Path.Combine(_root, "src", "blob.dat"), [0x52, 0x65, 0x74, 0x72, 0x79, 0, 0, 1, 2]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string P(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private SourceIndex Index(SourceIndexOptions? options = null) =>
        new(_root, options ?? new SourceIndexOptions { Watch = false, MaxAge = TimeSpan.FromHours(1) });

    [Fact]
    public async Task Only_project_text_is_indexed_never_dependencies_build_output_secrets_or_binaries()
    {
        using var index = Index();
        var snapshot = await index.GetSnapshotAsync();

        var paths = snapshot.Files.Select(f => f.RelativePath).ToList();
        Assert.Equal([P("src/RetryPolicy.cs"), P("src/Uploader.cs")], paths);
        Assert.Equal(2, snapshot.FilesRead);
    }

    [Fact]
    public async Task Gitignored_files_stay_out()
    {
        Write(".gitignore", "generated/\n");
        Write("generated/Client.cs", "public class GeneratedClient {}");

        using var index = Index();
        var snapshot = await index.GetSnapshotAsync();

        Assert.Null(snapshot.Find("generated/Client.cs"));
        Assert.Empty(snapshot.Definitions("GeneratedClient"));
    }

    [Fact]
    public async Task A_refresh_rereads_only_the_files_that_changed()
    {
        using var index = Index();
        var first = await index.GetSnapshotAsync();
        var unchanged = first.Find("src/RetryPolicy.cs");

        Write("src/Uploader.cs", """
            public class Uploader
            {
                public void UploadWithBackoff() { }
            }
            """);
        Write("src/Backoff.cs", "public static class Backoff {}");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "src", "Uploader.cs"), DateTime.UtcNow.AddMinutes(1));
        index.Invalidate();

        var second = await index.GetSnapshotAsync();

        Assert.NotSame(first, second);
        Assert.Equal(2, second.FilesRead);
        Assert.Same(unchanged, second.Find("src/RetryPolicy.cs"));
        Assert.NotEmpty(second.Definitions("UploadWithBackoff"));
        Assert.NotEmpty(second.Definitions("Backoff"));
        Assert.Empty(second.Definitions("Upload"));
    }

    [Fact]
    public async Task An_unchanged_project_answers_from_the_same_snapshot()
    {
        using var index = Index();
        var first = await index.GetSnapshotAsync();
        var second = await index.GetSnapshotAsync();

        Assert.Same(first, second);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_watcher_marks_the_snapshot_stale_when_a_file_is_written()
    {
        using var index = Index(new SourceIndexOptions { Watch = true });
        await index.GetSnapshotAsync();

        Write("src/Telemetry.cs", "public class TelemetrySink {}");

        SourceSnapshot snapshot;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            snapshot = await index.GetSnapshotAsync();
        }
        while (snapshot.Definitions("TelemetrySink").Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(20));

        Assert.NotEmpty(snapshot.Definitions("TelemetrySink"));
    }

    [Fact]
    public async Task An_index_let_go_of_still_answers_whoever_holds_it()
    {
        var index = Index(new SourceIndexOptions { Watch = true, MaxAge = TimeSpan.Zero });
        await index.GetSnapshotAsync();
        index.Dispose();

        Write("src/Late.cs", "public class LateArrival {}");
        var snapshot = await index.GetSnapshotAsync();

        Assert.NotEmpty(snapshot.Definitions("LateArrival"));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_scan()
    {
        using var index = Index();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => index.GetSnapshotAsync()));

        Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
    }

    [Fact]
    public async Task Grep_runs_line_by_line_in_path_order_and_honours_the_glob()
    {
        Write("web/retry.ts", "export function retry() {}\nconst x = retry();");
        using var index = Index();
        var snapshot = await index.GetSnapshotAsync();

        var hits = snapshot.Grep(new Regex("^.*retry", RegexOptions.IgnoreCase));
        Assert.Equal(
            [P("src/RetryPolicy.cs"), P("src/Uploader.cs"), P("web/retry.ts"), P("web/retry.ts")],
            hits.Select(h => h.RelativePath).ToList());
        Assert.Equal(1, hits[0].LineNumber);

        var typescript = snapshot.Grep(new Regex("retry", RegexOptions.IgnoreCase), "*.ts");
        Assert.All(typescript, h => Assert.EndsWith(".ts", h.RelativePath));
        Assert.Equal(2, typescript.Count);

        Assert.Single(snapshot.Grep(new Regex("retry", RegexOptions.IgnoreCase), maxResults: 1));
    }

    [Fact]
    public async Task Find_files_matches_at_any_depth_and_includes_non_text_files()
    {
        using var index = Index();
        var snapshot = await index.GetSnapshotAsync();

        Assert.Contains(P("src/blob.dat"), snapshot.FindFiles("*.dat"));
        Assert.Equal(2, snapshot.FindFiles("**/*.cs").Count);
    }

    [Fact]
    public async Task Prefix_lookup_finds_the_tokens_a_stem_starts()
    {
        using var index = Index();
        var snapshot = await index.GetSnapshotAsync();

        Assert.Contains("uploader", snapshot.TokensWithPrefix("upload"));
        Assert.Contains("retrypolicy", snapshot.TokensWithPrefix("retry"));
    }

    [Fact]
    public void Identifiers_are_indexed_whole_and_by_their_parts()
    {
        var tokens = SourceTokens.Of("HTTPClientFactory max_retry_count onKeyDown");

        Assert.Contains("httpclientfactory", tokens);
        Assert.Contains("http", tokens);
        Assert.Contains("client", tokens);
        Assert.Contains("factory", tokens);
        Assert.Contains("max_retry_count", tokens);
        Assert.Contains("retry", tokens);
        Assert.Contains("onkeydown", tokens);
        Assert.Contains("key", tokens);
        Assert.Contains("down", tokens);
    }

    [Fact]
    public void Counting_matches_the_tokens_of_each_occurrence()
    {
        var (keys, counts, total) = SourceTokens.Count("RetryPolicy.Execute(); RetryPolicy.Execute();");
        var map = keys.Zip(counts).ToDictionary(p => p.First, p => p.Second);

        Assert.Equal(2, map["retrypolicy"]);
        Assert.Equal(2, map["retry"]);
        Assert.Equal(2, map["execute"]);
        Assert.Equal(8, total);
    }

    [Theory]
    [InlineData("public sealed partial class SearchViewModel : ObservableObject", "SearchViewModel", SourceSymbolKind.Type)]
    [InlineData("internal sealed record DiscoveredRange", "DiscoveredRange", SourceSymbolKind.Type)]
    [InlineData("public readonly record struct LineSpan(int Start, int End);", "LineSpan", SourceSymbolKind.Type)]
    [InlineData("    public async Task<DiscoveryResult> RunAsync(string query, CancellationToken ct = default)", "RunAsync", SourceSymbolKind.Function)]
    [InlineData("    public int MaxRounds { get; init; } = 3;", "MaxRounds", SourceSymbolKind.Property)]
    [InlineData("    private static IReadOnlyList<string> Parse(string s) => [];", "Parse", SourceSymbolKind.Function)]
    [InlineData("export default function App() {", "App", SourceSymbolKind.Function)]
    [InlineData("export const handleKeyDown = async (event: KeyboardEvent) => {", "handleKeyDown", SourceSymbolKind.Function)]
    [InlineData("export interface UserSettings {", "UserSettings", SourceSymbolKind.Type)]
    [InlineData("def load_config(path):", "load_config", SourceSymbolKind.Function)]
    [InlineData("class ConfigLoader(Base):", "ConfigLoader", SourceSymbolKind.Type)]
    [InlineData("func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {", "ServeHTTP", SourceSymbolKind.Function)]
    [InlineData("pub fn parse_args() -> Args {", "parse_args", SourceSymbolKind.Function)]
    [InlineData("pub(crate) struct Settings {", "Settings", SourceSymbolKind.Type)]
    public void Declarations_are_found_across_languages(string line, string name, SourceSymbolKind kind)
    {
        var symbol = Assert.Single(SourceSymbols.Extract(line), s => s.Name == name);
        Assert.Equal(kind, symbol.Kind);
        Assert.Equal(1, symbol.Line);
    }

    [Theory]
    [InlineData("        var policy = new RetryPolicy(3);")]
    [InlineData("        RetryPolicy.Execute(() => Send());")]
    [InlineData("        if (ready) { Start(); }")]
    [InlineData("        return Execute(action);")]
    [InlineData("            new Dictionary<string, int>(),")]
    public void Calls_and_statements_are_not_declarations(string line) =>
        Assert.Empty(SourceSymbols.Extract(line));

    [Fact]
    public void Declarations_carry_their_line_numbers()
    {
        var symbols = SourceSymbols.Extract("using System;\n\nnamespace A;\n\npublic class Foo\n{\n    public void Bar() { }\n}\n");

        Assert.Contains(new SourceSymbol("Foo", SourceSymbolKind.Type, 5), symbols);
        Assert.Contains(new SourceSymbol("Bar", SourceSymbolKind.Function, 7), symbols);
    }
}
