using Codale.Core.Helper;

namespace Codale.Search.Tests;

/// <summary>
/// Drives discovery against a real repository on disk with a scripted model, so the
/// keyword → grep → pick → retry contract is tested without a model installed.
/// </summary>
public sealed class CodeDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-discovery-").FullName;

    public CodeDiscoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "tests"));

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
                public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(attempt);
            }
            """);

        File.WriteAllText(Path.Combine(_root, "src", "Player.js"), """
            function onKeyDown(e) {
                if (e.code === 'Space' && onGround) {
                    velocityY = -12;
                }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "tests", "RetryPolicyTests.cs"), """
            public class RetryPolicyTests { void Execute_retries() => RetryPolicy.Execute(() => {}); }
            """);
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

    private sealed class ScriptedModel(params string[] callsJson) : ISearchModel
    {
        private readonly Queue<string> _calls = new(callsJson);

        public List<(string Prompt, string Tool)> Seen { get; } = [];

        public Func<Exception>? Throw { get; init; }

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Seen.Add((conversation, tools.Single().Name));

            if (Throw is not null)
            {
                throw Throw();
            }

            return Task.FromResult(_calls.Count > 0 ? ToolCall.FromJson(_calls.Dequeue()) : null);
        }
    }

    private static string Keywords(string keywords, string paths = "") =>
        $$$"""{"tool":"search","arguments":{"keywords":"{{{keywords}}}","paths":"{{{paths}}}"}}""";

    private static string Pick(string relevant, string keywords = "") =>
        $$$"""{"tool":"pick","arguments":{"relevant":"{{{relevant}}}","keywords":"{{{keywords}}}"}}""";

    private CodeDiscovery Discovery(ISearchModel? model) => new(_root, model);

    [Fact]
    public async Task Only_the_files_the_model_picks_are_returned()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Backoff"), Pick("1"));

        var result = await Discovery(model).RunAsync("how does it recover from failed uploads");

        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine("src", "RetryPolicy.cs"), file.RelativePath);
        Assert.True(file.Confirmed);
        Assert.True(result.IsFinal);
        Assert.Contains("RetryPolicy", result.Keywords);
    }

    [Fact]
    public async Task The_model_sees_numbered_candidates_with_their_matching_lines()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Backoff"), Pick("1"));

        await Discovery(model).RunAsync("how does it back off");

        var judge = model.Seen[1];
        Assert.Equal("pick", judge.Tool);
        Assert.Contains("[1] src/RetryPolicy.cs", judge.Prompt);
        Assert.Contains("Backoff(int attempt)", judge.Prompt);
    }

    [Fact]
    public async Task The_keyword_step_sees_the_projects_real_file_names()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("1"));

        await Discovery(model).RunAsync("how does it back off");

        var keywordStep = model.Seen[0];
        Assert.Equal("search", keywordStep.Tool);
        Assert.Contains("src: Player.js, RetryPolicy.cs, Uploader.cs", keywordStep.Prompt);
        Assert.Contains("tests: RetryPolicyTests.cs", keywordStep.Prompt);
        Assert.EndsWith("Question: how does it back off", keywordStep.Prompt);
    }

    [Fact]
    public async Task Types_hidden_in_a_file_of_another_name_are_listed_for_the_keyword_step()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Helpers.cs"), """
            public static class ToolCallParser { }
            internal sealed record ParsedCall(string Name);
            """);
        var model = new ScriptedModel(Keywords("ToolCallParser"), Pick("1"));

        await Discovery(model).RunAsync("where is the reply parsed into a call");

        var prompt = model.Seen[0].Prompt;
        Assert.Contains("src/Helpers.cs: ToolCallParser, ParsedCall", prompt);

        // A type named like its file is already in the file list; test fixtures are noise.
        Assert.DoesNotContain("RetryPolicy.cs: RetryPolicy", prompt);
        Assert.DoesNotContain("RetryPolicyTests.cs:", prompt);
    }

    [Fact]
    public async Task A_file_the_model_names_reaches_the_judge_first_with_its_declarations()
    {
        // No keyword hits Player.js; the model names it from the file list.
        var model = new ScriptedModel(Keywords("Levitate", paths: "src/Player.js"), Pick("1"));

        var result = await Discovery(model).RunAsync("where is the jump");

        var judge = model.Seen[1];
        Assert.Contains("[1] src/Player.js", judge.Prompt);
        Assert.Contains("1: function onKeyDown(e) {", judge.Prompt);
        Assert.Equal(Path.Combine("src", "Player.js"), Assert.Single(result.Files).RelativePath);
    }

    [Fact]
    public void Model_paths_match_whole_files_in_either_slash_style()
    {
        var path = Path.Combine("src", "App", "Settings.cs");

        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("src/App/Settings.cs"), path));
        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("./src/App/Settings.cs"), path));
        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("App\\Settings.cs"), path));
        Assert.False(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("ings.cs"), path));
        Assert.False(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("src/App"), path));
    }

    [Fact]
    public void A_big_project_lists_fewer_files_per_folder_to_fit()
    {
        var files = Enumerable.Range(0, 400).Select(i => Path.Combine("src", $"Component{i:000}.cs"))
            .Append(Path.Combine("assets", "logo.png"))
            .ToList();

        var list = CodeDiscovery.BuildFileList(files, maxChars: 2000);

        Assert.True(list.Length <= 2000);
        Assert.Contains("src: Component000.cs", list);
        Assert.Contains("more", list);
        Assert.DoesNotContain("logo.png", list);
    }

    [Fact]
    public async Task Tests_rank_below_the_code_they_test()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Execute"), Pick(""));

        var result = await Discovery(model).RunAsync("where is the retry policy");

        var order = result.Files.Select(f => f.RelativePath).ToList();
        Assert.Equal(Path.Combine("src", "RetryPolicy.cs"), order[0]);
        Assert.True(order.IndexOf(Path.Combine("tests", "RetryPolicyTests.cs")) is -1 or > 0);
    }

    [Fact]
    public async Task No_pick_retries_with_the_models_new_keywords()
    {
        var model = new ScriptedModel(
            Keywords("RetryPolicy"),
            Pick("", keywords: "velocityY, onGround"),
            Pick("1"));

        var result = await Discovery(model).RunAsync("how does the player jump");

        Assert.Equal(3, model.Seen.Count);
        Assert.Equal(2, result.Round);
        Assert.Equal(Path.Combine("src", "Player.js"), Assert.Single(result.Files).RelativePath);

        // Files rejected in round one are not offered again.
        Assert.DoesNotContain("RetryPolicy.cs", model.Seen[2].Prompt);
    }

    [Fact]
    public async Task Rounds_stop_at_the_cap_and_return_the_best_guess_unconfirmed()
    {
        var model = new ScriptedModel(
            Keywords("RetryPolicy"),
            Pick("", "Backoff"),
            Pick("", "velocityY"),
            Pick("", "never"));

        var result = await new CodeDiscovery(_root, model) { MaxRounds = 2 }.RunAsync("anything");

        Assert.Equal(3, model.Seen.Count);
        Assert.True(result.IsFinal);
        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, f => Assert.False(f.Confirmed));
    }

    [Fact]
    public async Task A_round_whose_keywords_miss_still_offers_the_instant_files_to_the_judge()
    {
        // "appleColor"-style miss: the model's first keywords match nothing, so without
        // the instant pool the judge would see no candidates at all and confirm junk.
        var model = new ScriptedModel(Keywords("NotThere anywhere"), Pick("1"));

        var result = await Discovery(model).RunAsync("how does it back off");

        Assert.Contains("[1] src/RetryPolicy.cs", model.Seen[1].Prompt);
        Assert.Equal(Path.Combine("src", "RetryPolicy.cs"), Assert.Single(result.Files).RelativePath);
        Assert.True(result.Files[0].Confirmed);
    }

    [Fact]
    public async Task Provisional_results_arrive_before_the_model_answers()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("1"));
        var discovery = Discovery(model);
        var updates = new List<DiscoveryResult>();
        discovery.Progress += (_, r) => updates.Add(r);

        await discovery.RunAsync("retry upload");

        Assert.Equal(0, updates[0].Round);
        Assert.False(updates[0].IsFinal);
        Assert.NotEmpty(updates[0].Files);
    }

    [Fact]
    public async Task Without_a_model_the_question_words_are_searched()
    {
        var result = await Discovery(null).RunAsync("where is Backoff");

        Assert.True(result.IsFinal);
        Assert.Equal(Path.Combine("src", "RetryPolicy.cs"), result.Files[0].RelativePath);
    }

    [Fact]
    public async Task A_failing_model_degrades_to_the_instant_results()
    {
        var model = new ScriptedModel { Throw = () => new IOException("pipe broke") };

        var result = await Discovery(model).RunAsync("where is Backoff");

        Assert.True(result.IsFinal);
        Assert.Equal(Path.Combine("src", "RetryPolicy.cs"), result.Files[0].RelativePath);
    }

    [Fact]
    public async Task Ranges_mark_the_matching_lines()
    {
        var model = new ScriptedModel(Keywords("velocityY"), Pick("1"));

        var result = await Discovery(model).RunAsync("jump");

        var range = Assert.Single(Assert.Single(result.Files).Ranges);
        Assert.Equal([3], range.MatchLines);
        Assert.Contains("velocityY = -12", range.Code);
    }

    private static Dictionary<int, int> Weighted(params int[] matchLines) => matchLines.ToDictionary(n => n, _ => 1);

    [Fact]
    public void Distant_matches_become_separate_ranges_in_file_order()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();

        var ranges = CodeDiscovery.Slice(lines, Weighted(10, 12, 150));

        Assert.Equal(2, ranges.Count);
        Assert.Equal((7, 15), (ranges[0].StartLine, ranges[0].EndLine));
        Assert.Equal([10, 12], ranges[0].MatchLines);
        Assert.Equal((147, 153), (ranges[1].StartLine, ranges[1].EndLine));
    }

    [Fact]
    public void A_name_only_match_shows_the_top_of_the_file()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray();

        var range = Assert.Single(CodeDiscovery.Slice(lines, Weighted()));

        Assert.Equal((1, 30), (range.StartLine, range.EndLine));
        Assert.Empty(range.MatchLines);
    }

    /// <summary>An Electron app with no .gitignore and a node_modules full of "run".</summary>
    private string ElectronProject()
    {
        var root = Directory.CreateTempSubdirectory("codale-electron-").FullName;

        File.WriteAllText(Path.Combine(root, "package.json"), """
            {
              "name": "snake",
              "main": "main.js",
              "scripts": {
                "start": "electron ."
              }
            }
            """);
        File.WriteAllText(Path.Combine(root, "main.js"), """
            const { app, BrowserWindow } = require('electron');
            function createWindow() { new BrowserWindow().loadFile('index.html'); }
            app.whenReady().then(createWindow);
            """);
        File.WriteAllText(Path.Combine(root, "game.js"), "let snake = [];");

        for (var i = 0; i < 30; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "node_modules", $"pkg{i}")).FullName;
            File.WriteAllText(Path.Combine(dir, "index.js"), "// run the project runner\nfunction run(project) { runProject(project); }");
            File.WriteAllText(Path.Combine(dir, "package.json"), """{ "scripts": { "start": "node run.js" } }""");
        }

        return root;
    }

    [Fact]
    public async Task Setup_questions_land_on_the_manifest_and_entry_point_not_node_modules()
    {
        var root = ElectronProject();
        try
        {
            var result = await new CodeDiscovery(root, null).RunAsync("how to run the project");

            Assert.Equal(["package.json", "main.js"], result.Files.Take(2).Select(f => f.RelativePath));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("node_modules"));
            Assert.Contains(result.Files[0].Ranges.SelectMany(r => r.MatchLines), n => n is 3 or 4 or 5);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Setup_candidates_reach_the_model_even_without_keyword_matches()
    {
        var root = ElectronProject();
        try
        {
            var model = new ScriptedModel(Keywords("npmStart, launchApp"), Pick("1, 2"));

            var result = await new CodeDiscovery(root, model).RunAsync("how do I start the app");

            Assert.Contains("[1] package.json", model.Seen[1].Prompt);
            Assert.DoesNotContain("node_modules", model.Seen[1].Prompt);
            Assert.Equal(["package.json", "main.js"], result.Files.Select(f => f.RelativePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_keyword_common_to_many_files_does_not_bring_them_all_in()
    {
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(_root, "src", $"Widget{i}.js"), "let state = {};\nstate.ready = true;");
        }

        var model = new ScriptedModel(Keywords("state, velocityY"), Pick(""));

        var result = await Discovery(model).RunAsync("how does the player jump");

        Assert.Equal(Path.Combine("src", "Player.js"), Assert.Single(result.Files).RelativePath);
    }

    [Fact]
    public async Task Short_keywords_only_match_at_the_start_of_a_word()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Text.js"), "function truncate(s) { return s.slice(0, 3); }");
        File.WriteAllText(Path.Combine(_root, "src", "Runner.js"), "function run() { runGame(); }");

        var result = await Discovery(null).RunAsync("run");

        Assert.Contains(result.Files, f => f.RelativePath.EndsWith("Runner.js"));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Text.js"));
    }

    [Fact]
    public async Task The_model_cannot_pick_everything()
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(Path.Combine(_root, "src", $"RetryPolicy{i}.cs"), "public static class RetryPolicy { }");
        }

        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("1, 2, 3, 4, 5, 6, 7, 8"));

        var result = await Discovery(model).RunAsync("retry");

        Assert.Equal(4, result.Files.Count);
    }

    [Fact]
    public void A_stray_weak_match_is_not_shown_beside_a_strong_cluster()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();

        var ranges = CodeDiscovery.Slice(lines, new Dictionary<int, int> { [10] = 1000, [11] = 1000, [150] = 80 });

        Assert.Equal(10, Assert.Single(ranges).MatchLines[0]);
    }

    [Fact]
    public void Filler_words_are_dropped_from_the_question() =>
        Assert.Equal(["snake", "speed"], CodeDiscovery.QuestionWords("how does the project set the snake speed"));

    [Theory]
    [InlineData(@"node_modules\electron\index.js", true)]
    [InlineData(@"web\node_modules\x\a.js", true)]
    [InlineData(@"src\bin\Debug\a.dll", true)]
    [InlineData(@"src\binary.js", false)]
    [InlineData(@"main.js", false)]
    public void Dependency_and_output_folders_are_noise(string path, bool expected) =>
        Assert.Equal(expected, CodeDiscovery.IsNoisePath(path));

    [Theory]
    [InlineData(@"tests\Codale.Search.Tests\Foo.cs", true)]
    [InlineData(@"src\Codale.Search.Tests\Foo.cs", true)]
    [InlineData(@"src\RetryPolicyTests.cs", true)]
    [InlineData(@"web\player.test.ts", true)]
    [InlineData(@"README.md", true)]
    [InlineData(@"src\latest.js", false)]
    [InlineData(@"src\Inspector.cs", false)]
    [InlineData(@"src\Contest.cs", false)]
    public void Test_and_doc_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, CodeDiscovery.IsTestOrDoc(path));
}
