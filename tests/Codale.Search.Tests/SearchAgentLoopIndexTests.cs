using Codale.Core.Helper;

namespace Codale.Search.Tests;

/// <summary>The explore loop with a source index behind it: ranked opening, in-memory tools, symbol lookup.</summary>
public sealed class SearchAgentLoopIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-loop-index-").FullName;
    private readonly SourceIndex _index;

    public SearchAgentLoopIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Uploader.cs"), """
            public class Uploader
            {
                public void Upload()
                {
                    // retry with backoff when the upload fails
                    RetryPolicy.Execute(() => Send());
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "src", "RetryPolicy.cs"), """
            public static class RetryPolicy
            {
                public static void Execute(Action action) { }
            }
            """);
        File.WriteAllText(Path.Combine(_root, ".env"), "API_KEY=hunter2marker");

        _index = new SourceIndex(_root, new SourceIndexOptions { Watch = false, MaxAge = TimeSpan.FromHours(1) });
    }

    public void Dispose()
    {
        _index.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ScriptedModel(params string[] callsJson) : ISearchModel
    {
        private readonly Queue<ToolCall> _calls = new(callsJson.Select(j => ToolCall.FromJson(j)!));

        public List<string> SeenConversations { get; } = [];

        public List<string> SeenSystemPrompts { get; } = [];

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            SeenSystemPrompts.Add(systemPrompt);
            SeenConversations.Add(conversation);
            return Task.FromResult(_calls.Count > 0 ? _calls.Dequeue() : null);
        }
    }

    private SearchAgentLoop Loop(ISearchModel model) => new(_root, model) { Index = _index };

    [Fact]
    public async Task The_opening_move_is_the_index_ranking_with_related_source()
    {
        var model = new ScriptedModel("""{"tool":"answer","arguments":{"summary":"RetryPolicy.Execute"}}""");

        var answer = await Loop(model).RunAsync("how does the Uploader upload");

        Assert.StartsWith("rank source", answer.Trace[0].Description);
        var conversation = Assert.Single(model.SeenConversations);
        Assert.Contains("Source index ranking", conversation);
        Assert.Contains("src/Uploader.cs", conversation);
        Assert.Contains("defines RetryPolicy, used by Uploader.cs", conversation);
        Assert.Contains("symbol", model.SeenSystemPrompts[0]);

        Assert.Contains(answer.Sections, s => s.RelativePath.EndsWith("Uploader.cs"));
        Assert.Contains(answer.Sections, s => s.RelativePath.EndsWith("RetryPolicy.cs") && s.Code.Contains("class RetryPolicy"));
    }

    [Fact]
    public async Task The_symbol_tool_reports_declarations_and_users()
    {
        var model = new ScriptedModel(
            """{"tool":"symbol","arguments":{"name":"RetryPolicy"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("retry");

        var last = model.SeenConversations[^1];
        Assert.Contains("symbol RetryPolicy is declared in:", last);
        Assert.Contains("src/RetryPolicy.cs:1: public static class RetryPolicy", last);
        Assert.Contains("src/Uploader.cs (1)", last);
    }

    [Fact]
    public async Task Grep_runs_in_memory_and_never_reaches_secrets()
    {
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"hunter2marker|Execute\\("}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("execute");

        var last = model.SeenConversations[^1];
        Assert.Contains("grep \"hunter2marker|Execute\\(\" returned 2 matches", last);
        Assert.All(model.SeenConversations, c => Assert.DoesNotContain("API_KEY", c));
        Assert.DoesNotContain(answer.Hits, h => h.RelativePath == ".env");
    }

    [Fact]
    public async Task A_pattern_the_index_cannot_compile_still_reports_ripgreps_error()
    {
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"("}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("execute");

        Assert.Contains("failed", model.SeenConversations[^1]);
    }

    [Fact]
    public async Task Find_files_lists_from_the_snapshot()
    {
        var model = new ScriptedModel(
            """{"tool":"find_files","arguments":{"glob":"*.cs"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("source files");

        Assert.Contains("find_files *.cs returned 2 paths", model.SeenConversations[^1]);
    }
}
