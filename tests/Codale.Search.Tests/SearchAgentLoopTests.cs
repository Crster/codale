using Codale.Core.Helper;

namespace Codale.Search.Tests;

/// <summary>
/// Exercises the loop against a real repository on disk with a scripted model, so the
/// controller's guarantees - budgets, the deterministic first grep, loop detection - are
/// tested without depending on a model being installed or on what it happens to decide.
/// </summary>
public sealed class SearchAgentLoopTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-search-").FullName;

    public SearchAgentLoopTests()
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

        File.WriteAllText(Path.Combine(_root, "README.md"), "A project about uploading things.");
    }

    /// <summary>A model that returns a fixed script of calls, so the loop is deterministic.</summary>
    private sealed class ScriptedModel : ISearchModel
    {
        private readonly Queue<ToolCall> _calls;

        public ScriptedModel(params string[] callsJson) =>
            _calls = new Queue<ToolCall>(callsJson.Select(j => ToolCall.FromJson(j)!));

        public int Invocations { get; private set; }

        public List<string> SeenConversations { get; } = [];

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Invocations++;
            SeenConversations.Add(conversation);
            return Task.FromResult(_calls.Count > 0 ? _calls.Dequeue() : null);
        }
    }

    /// <summary>A scripted model that also writes, recording what it was asked to write from.</summary>
    private sealed class WritingModel(string reply, params string[] callsJson) : ISearchModel
    {
        private readonly ScriptedModel _inner = new(callsJson);

        public string? WritePrompt { get; private set; }

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
            _inner.NextCallAsync(systemPrompt, conversation, tools, ct);

        public Task<string> WriteAsync(string systemPrompt, string prompt, CancellationToken ct)
        {
            WritePrompt = prompt;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Each_recorded_step_is_reported_as_it_happens()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":"src/RetryPolicy.cs","start":1,"end":4}}""",
            """{"tool":"answer","arguments":{"summary":"RetryPolicy"}}""");
        var seen = new List<SearchStep>();

        var answer = await new SearchAgentLoop(_root, model)
        {
            Budget = TimeSpan.FromSeconds(30),
            OnStep = seen.Add,
        }.RunAsync("where is retry handled?");

        Assert.NotEmpty(seen);
        Assert.Equal(answer.Trace.Select(s => s.Number), seen.Select(s => s.Number));
    }

    [Fact]
    public async Task The_answer_cites_the_sections_the_model_read_first()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":"src/RetryPolicy.cs","start":1,"end":4}}""",
            """{"tool":"answer","arguments":{"summary":"RetryPolicy"}}""");

        var answer = await Loop(model).RunAsync("where is retry handled?");

        var first = answer.Sections[0];
        Assert.Contains("RetryPolicy.cs", first.RelativePath);
        Assert.Equal(1, first.StartLine);
        Assert.Contains("public static class RetryPolicy", first.Code);
        Assert.Equal("read by the model", first.Reason);

        // Same file, overlapping lines: not cited twice.
        Assert.Single(answer.Sections, s => s.RelativePath.Contains("RetryPolicy.cs"));
    }

    [Fact]
    public async Task The_write_up_is_generated_from_the_cited_sections()
    {
        var model = new WritingModel(
            "<think>hmm</think>\nRetries are in `RetryPolicy`.",
            """{"tool":"answer","arguments":{"summary":"RetryPolicy"}}""");

        var answer = await Loop(model).RunAsync("where is retry handled?");

        Assert.Equal("Retries are in `RetryPolicy`.", answer.Explanation);
        Assert.Contains("Question: where is retry handled?", model.WritePrompt);
        Assert.Contains("RetryPolicy.Execute", model.WritePrompt);
    }

    [Fact]
    public async Task Setup_questions_pull_in_the_project_manifests()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), """
            {
              "name": "demo",
              "scripts": {
                "start": "vite"
              }
            }
            """);

        var model = new ScriptedModel("""{"tool":"answer","arguments":{"summary":"npm start"}}""");
        var answer = await Loop(model).RunAsync("how to run this app");

        Assert.Contains(answer.Sections, s => s.RelativePath == "package.json" && s.Code.Contains("\"start\""));
        Assert.Contains("package.json", model.SeenConversations[0]);
    }

    private SearchAgentLoop Loop(ISearchModel model, int maxSteps = 12) =>
        new(_root, model) { MaxSteps = maxSteps, Budget = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task The_first_grep_runs_before_the_model_gets_a_turn()
    {
        // The model immediately gives up; the seed grep must still have produced hits.
        var model = new ScriptedModel();
        var answer = await Loop(model).RunAsync("where is retry handled?");

        Assert.NotEmpty(answer.Hits);
        Assert.Contains(answer.Hits, h => h.RelativePath.Contains("Uploader") || h.RelativePath.Contains("RetryPolicy"));

        var firstStep = answer.Trace[0];
        Assert.Equal(SearchStepKind.Tool, firstStep.Kind);
        Assert.StartsWith("grep", firstStep.Description);
    }

    [Fact]
    public async Task The_model_sees_the_seed_findings_on_its_first_turn()
    {
        var model = new ScriptedModel("""{"tool":"answer","arguments":{"summary":"done"}}""");
        await Loop(model).RunAsync("where is retry handled?");

        var conversation = Assert.Single(model.SeenConversations);
        Assert.Contains("Question: where is retry handled?", conversation);
        Assert.Contains("Findings so far:", conversation);
        Assert.Contains("RetryPolicy", conversation);
    }

    [Fact]
    public async Task Calling_answer_ends_the_loop_immediately()
    {
        var model = new ScriptedModel(
            """{"tool":"answer","arguments":{"summary":"Retries live in RetryPolicy.cs"}}""",
            """{"tool":"grep","arguments":{"pattern":"never reached"}}""");

        var answer = await Loop(model).RunAsync("where is retry handled?");

        Assert.Equal("Retries live in RetryPolicy.cs", answer.Summary);
        Assert.False(answer.StoppedEarly);
        Assert.Equal(1, model.Invocations);
        Assert.Equal(SearchStepKind.Answer, answer.Trace[^1].Kind);
    }

    [Fact]
    public async Task A_repeated_identical_call_is_refused_rather_than_run_again()
    {
        // Looping on one grep is the characteristic small-model failure.
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"backoff"}}""",
            """{"tool":"grep","arguments":{"pattern":"backoff"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("retry");

        Assert.Contains(answer.Trace, s => s.Kind == SearchStepKind.Note && s.Description.Contains("repeated"));
    }

    [Fact]
    public async Task Repeated_refusals_in_a_row_end_the_loop_instead_of_spending_the_budget()
    {
        // Live runs showed the model re-proposing the exact same call until the step cap;
        // after three refusals the loop must stop rather than burn the remaining steps.
        var calls = Enumerable.Repeat(
            """{"tool":"grep","arguments":{"pattern":"backoff"}}""", 10).ToArray();

        var answer = await Loop(new ScriptedModel(calls), maxSteps: 12).RunAsync("retry");

        // Seed grep + first grep + three refusals: the model was asked five times in
        // total, not twelve.
        Assert.True(answer.StoppedEarly);
        Assert.True(answer.Trace.Count <= 5, $"trace: {answer.Trace.Count} steps");
    }

    [Fact]
    public async Task Running_out_of_steps_still_returns_what_was_found()
    {
        // A model that greps forever and never answers.
        var calls = Enumerable.Range(0, 20)
            .Select(i => "{\"tool\":\"grep\",\"arguments\":{\"pattern\":\"upload" + i + "\"}}")
            .ToArray();

        var answer = await Loop(new ScriptedModel(calls), maxSteps: 4).RunAsync("where is retry handled?");

        Assert.True(answer.StoppedEarly);
        Assert.NotEmpty(answer.Hits);
        Assert.True(answer.Trace.Count <= 4);
    }

    /// <summary>A model that answers each round with a batch of calls.</summary>
    private sealed class BatchingModel(params string[][] rounds) : ISearchModel
    {
        private readonly Queue<string[]> _rounds = new(rounds);

        public int Rounds { get; private set; }

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
            Task.FromResult<ToolCall?>(null);

        public Task<IReadOnlyList<ToolCall>> NextCallsAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Rounds++;
            IReadOnlyList<ToolCall> calls = _rounds.Count > 0 ? [.. _rounds.Dequeue().Select(j => ToolCall.FromJson(j)!)] : [];
            return Task.FromResult(calls);
        }
    }

    [Fact]
    public async Task Calls_made_together_run_in_one_round_and_an_answer_beside_them_lands_after()
    {
        var model = new BatchingModel(
        [
            """{"tool":"grep","arguments":{"pattern":"Execute"}}""",
            """{"tool":"read_file","arguments":{"path":"src/RetryPolicy.cs","start":1,"end":4}}""",
            """{"tool":"answer","arguments":{"summary":"RetryPolicy.Execute"}}""",
        ]);

        var answer = await Loop(model).RunAsync("where is retry handled?");

        Assert.Equal(1, model.Rounds);
        Assert.False(answer.StoppedEarly);
        Assert.Equal("RetryPolicy.Execute", answer.Summary);
        Assert.Equal("read by the model", answer.Sections[0].Reason);
    }

    /// <summary>Greps until the step cap, then answers when only the answer tool is offered.</summary>
    private sealed class ConcludingModel : ISearchModel
    {
        private int _grep;

        public IReadOnlyList<ToolDefinition>? ClosingTools { get; private set; }

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            if (tools.Count == 1)
            {
                ClosingTools = tools;
                return Task.FromResult(ToolCall.FromJson("""{"tool":"answer","arguments":{"summary":"RetryPolicy, from the greps"}}"""));
            }

            return Task.FromResult(ToolCall.FromJson("{\"tool\":\"grep\",\"arguments\":{\"pattern\":\"retry" + _grep++ + "\"}}"));
        }
    }

    [Fact]
    public async Task Running_out_of_steps_ends_with_a_closing_answer_instead_of_raw_hits()
    {
        var model = new ConcludingModel();

        var answer = await Loop(model, maxSteps: 4).RunAsync("where is retry handled?");

        Assert.Equal("answer", Assert.Single(model.ClosingTools!).Name);
        Assert.False(answer.StoppedEarly);
        Assert.Equal("RetryPolicy, from the greps", answer.Summary);
        Assert.Equal(SearchStepKind.Answer, answer.Trace[^1].Kind);
    }

    [Fact]
    public async Task Read_file_cannot_escape_the_project()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":"../../../Windows/System32/drivers/etc/hosts","start":1}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("hosts");

        // The refusal is reported to the model rather than silently succeeding.
        Assert.Contains(model.SeenConversations[^1], c => true);
        Assert.Contains("outside the project", model.SeenConversations[^1]);
    }

    [Fact]
    public async Task Reading_a_missing_file_is_reported_not_thrown()
    {
        var model = new ScriptedModel(
            """{"tool":"read_file","arguments":{"path":"nope.cs","start":1}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("nope");

        Assert.Equal("done", answer.Summary);
        Assert.Contains("does not exist", model.SeenConversations[^1]);
    }

    [Fact]
    public async Task Find_files_lists_matching_paths()
    {
        var model = new ScriptedModel(
            """{"tool":"find_files","arguments":{"glob":"**/*.cs"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        await Loop(model).RunAsync("source files");

        Assert.Contains("Uploader.cs", model.SeenConversations[^1]);
    }

    [Fact]
    public async Task An_unknown_tool_is_reported_rather_than_crashing()
    {
        var model = new ScriptedModel(
            """{"tool":"rm_rf","arguments":{"path":"/"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("anything");

        Assert.Equal("done", answer.Summary);
        Assert.Contains("no tool called rm_rf", model.SeenConversations[^1]);
    }

    [Fact]
    public async Task Results_are_deduplicated_and_ranked_by_file()
    {
        var model = new ScriptedModel(
            """{"tool":"grep","arguments":{"pattern":"retry"}}""",
            """{"tool":"grep","arguments":{"pattern":"Retry"}}""",
            """{"tool":"answer","arguments":{"summary":"done"}}""");

        var answer = await Loop(model).RunAsync("retry");

        var duplicates = answer.Hits
            .GroupBy(h => (h.FilePath, h.LineNumber))
            .Where(g => g.Count() > 1);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void The_seed_grep_keeps_a_quoted_phrase_whole()
    {
        var pattern = SearchAgentLoop.SeedPattern("where is \"public static void main\" in the launcher");

        Assert.Equal(@"public\s+static\s+void\s+main|launcher", pattern);
        Assert.Matches(pattern, "    public  static void main(String[] args)");
    }

    [Theory]
    [InlineData("where do we handle retry on a failed upload?", "retry")]
    [InlineData("what is the RetryPolicy?", "RetryPolicy")]
    public void Keyword_extraction_drops_question_words(string query, string expected)
    {
        var keywords = SearchAgentLoop.KeywordsFrom(query);

        Assert.Contains(expected, keywords, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("where", keywords, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("what", keywords, StringComparison.OrdinalIgnoreCase);
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
}
