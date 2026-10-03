using Codale.Core.Helper;

namespace Codale.Search.Tests;

/// <summary>
/// What the model may and may not be shown: nothing outside the project, no credentials
/// files, and an honest error when ripgrep rejects a pattern.
/// </summary>
public sealed class SearchHardeningTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("codale-search-").FullName;
    private readonly string _root;

    public SearchHardeningTests()
    {
        _root = Path.Combine(_parent, "proj");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.cs"), "class App { string Name = \"hunter2marker\"; }");

        // No .gitignore anywhere: ripgrep would happily search these.
        File.WriteAllText(Path.Combine(_root, ".env"), "API_KEY=hunter2marker\n");
        File.WriteAllText(Path.Combine(_root, "server.pem"), "-----BEGIN PRIVATE KEY-----hunter2marker\n");

        Directory.CreateDirectory(Path.Combine(_parent, "proj-secrets"));
        File.WriteAllText(Path.Combine(_parent, "proj-secrets", "notes.txt"), "sibling-only-content");
    }

    private sealed class ScriptedModel(params string[] callsJson) : ISearchModel
    {
        private readonly Queue<ToolCall> _calls = new(callsJson.Select(j => ToolCall.FromJson(j)!));

        public List<string> SeenConversations { get; } = [];

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            SeenConversations.Add(conversation);
            return Task.FromResult(_calls.Count > 0 ? _calls.Dequeue() : null);
        }
    }

    private SearchAgentLoop Loop(ISearchModel model) =>
        new(_root, model) { Budget = TimeSpan.FromSeconds(30) };

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("config/.ENV.production")]
    [InlineData("certs/server.pem")]
    [InlineData("tls.key")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519.pub")]
    [InlineData("cert.pfx")]
    [InlineData("credentials.json")]
    [InlineData(".npmrc")]
    [InlineData("secrets.yaml")]
    [InlineData("home/.ssh/config")]
    [InlineData("deploy\\.aws\\config")]
    public void Credential_files_are_recognised(string path) =>
        Assert.True(SensitivePaths.IsSensitive(path));

    [Theory]
    [InlineData("app.cs")]
    [InlineData("src/environment.cs")]
    [InlineData("README.md")]
    [InlineData("keys.md")]
    [InlineData("package.json")]
    public void Ordinary_files_are_not(string path) =>
        Assert.False(SensitivePaths.IsSensitive(path));

    [Fact]
    public async Task The_model_cannot_read_a_credentials_file()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":".env"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("what is the api key");

        var last = model.SeenConversations[^1];
        Assert.Contains(".env looks like a secrets file", last);
        Assert.DoesNotContain("API_KEY=hunter2marker", last);
        Assert.DoesNotContain(answer.Sections, s => s.RelativePath == ".env");
    }

    [Fact]
    public async Task Credential_files_do_not_leak_through_grep_results()
    {
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"hunter2marker"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("hunter2marker");

        Assert.Contains(answer.Hits, h => h.RelativePath == "app.cs");
        Assert.DoesNotContain(answer.Hits, h => SensitivePaths.IsSensitive(h.RelativePath));
        Assert.All(model.SeenConversations, c => Assert.DoesNotContain("API_KEY", c));
        Assert.All(model.SeenConversations, c => Assert.DoesNotContain("PRIVATE KEY", c));
    }

    [Fact]
    public async Task A_sibling_folder_sharing_the_root_as_a_prefix_is_outside_the_project()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":"../proj-secrets/notes.txt"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("notes");

        var last = model.SeenConversations[^1];
        Assert.Contains("is outside the project", last);
        Assert.DoesNotContain("sibling-only-content", last);
    }

    [Fact]
    public async Task A_rejected_pattern_is_reported_to_the_model_not_read_as_no_matches()
    {
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"("}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("anything at all");

        Assert.Contains("failed", model.SeenConversations[^1]);
        Assert.DoesNotContain("returned 0 matches", model.SeenConversations[^1].Split("grep \"(\"")[^1]);
    }

    [Fact(Timeout = 30_000)]
    public async Task Ripgrep_rejecting_a_pattern_surfaces_as_an_error()
    {
        var search = new RipgrepSearch(_root);

        await Assert.ThrowsAsync<RipgrepSearchException>(async () =>
        {
            await foreach (var _ in search.SearchAsync(new SearchQuery { Text = "(", IsRegex = true }))
            {
            }
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task A_search_with_no_matches_is_still_just_empty()
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in new RipgrepSearch(_root).SearchAsync(new SearchQuery { Text = "zzzznomatchzzzz" }))
        {
            hits.Add(hit);
        }

        Assert.Empty(hits);
    }

    [Fact(Timeout = 30_000)]
    public async Task Hits_carry_absolute_and_root_relative_paths()
    {
        var hit = await new RipgrepSearch(_root).SearchAsync(new SearchQuery { Text = "class App" }).SingleAsync();

        Assert.Equal("app.cs", hit.RelativePath);
        Assert.Equal(Path.Combine(_root, "app.cs"), hit.FilePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
