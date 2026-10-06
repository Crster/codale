using Codale.Core.Helper;

namespace Codale.Search.Tests;

/// <summary>
/// The explorer over a real project on disk: ranking, related-source expansion, smart
/// ranges, and the keyword → pick contract with a scripted background-task model.
/// </summary>
public sealed class SourceExplorerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-explorer-").FullName;
    private readonly SourceIndex _index;

    public SourceExplorerTests()
    {
        Write("src/Uploader.cs", """
            using System;

            public class Uploader
            {
                private readonly HttpGateway _gateway = new();

                public void Upload(string path)
                {
                    // retry with backoff when the upload fails
                    RetryPolicy.Execute(() => _gateway.Send(path));
                }
            }
            """);

        Write("src/RetryPolicy.cs", """
            using System;

            /// <summary>Retries an action with exponential backoff.</summary>
            public static class RetryPolicy
            {
                public static void Execute(Action action)
                {
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            action();
                            return;
                        }
                        catch (Exception) when (attempt < 3)
                        {
                            Thread.Sleep(Backoff(attempt));
                        }
                    }
                }

                public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));
            }
            """);

        Write("src/Net/HttpGateway.cs", """
            public sealed class HttpGateway
            {
                public void Send(string path)
                {
                    // network call
                }
            }
            """);

        Write("src/SyncJob.cs", """
            public class SyncJob
            {
                public void Run() => RetryPolicy.Execute(() => Sync());

                private void Sync() { }
            }
            """);

        Write("web/player.js", """
            function onKeyDown(e) {
                if (e.code === 'Space' && onGround) {
                    velocityY = -12;
                }
            }
            """);

        Write("tests/RetryPolicyTests.cs", """
            public class RetryPolicyTests { void Execute_retries() => RetryPolicy.Execute(() => {}); }
            """);

        Write("docs/retry.md", "# Retry\nThe RetryPolicy retries uploads with backoff. RetryPolicy RetryPolicy.");

        Write("package.json", """
            {
              "name": "demo",
              "scripts": {
                "start": "node web/player.js"
              }
            }
            """);

        Write("node_modules/retry/index.js", "module.exports = function RetryPolicy() { retry(); retry(); };");
        Write(".env", "RETRY_API_KEY=hunter2marker");

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

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string P(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private sealed class ScriptedModel(params string[] callsJson) : ISearchModel
    {
        private readonly Queue<string> _calls = new(callsJson);

        public List<(string Prompt, string Tool)> Seen { get; } = [];

        public Func<Exception>? Throw { get; init; }

        public TaskCompletionSource? Gate { get; init; }

        public async Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Seen.Add((conversation, tools.Single().Name));

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }

            if (Throw is not null)
            {
                throw Throw();
            }

            return _calls.Count > 0 ? ToolCall.FromJson(_calls.Dequeue()) : null;
        }
    }

    private static string Keywords(string keywords, string paths = "") =>
        $$$"""{"tool":"search","arguments":{"keywords":"{{{keywords}}}","paths":"{{{paths}}}"}}""";

    private static string Pick(string relevant, string keywords = "") =>
        $$$"""{"tool":"pick","arguments":{"relevant":"{{{relevant}}}","keywords":"{{{keywords}}}"}}""";

    private SourceExplorer Explorer(ISearchModel? model = null) => new(_index, model);

    [Fact]
    public async Task The_declaring_file_ranks_first_and_tests_and_docs_rank_below_code()
    {
        var result = await Explorer().RunAsync("where is the RetryPolicy backoff");

        var paths = result.Files.Select(f => f.RelativePath).ToList();
        Assert.Equal(P("src/RetryPolicy.cs"), paths[0]);
        Assert.True(paths.IndexOf(P("tests/RetryPolicyTests.cs")) is -1 or > 1);
        Assert.True(paths.IndexOf(P("docs/retry.md")) is -1 or > 1);
        Assert.True(result.IsFinal);
    }

    [Fact]
    public async Task Dependencies_secrets_and_noise_never_surface()
    {
        var result = await Explorer().RunAsync("retry");

        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("node_modules"));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".env");
        Assert.All(result.Files, f => Assert.All(f.Ranges, r => Assert.DoesNotContain("hunter2marker", r.Code)));
    }

    [Fact]
    public async Task The_code_a_match_uses_joins_it_as_related_source()
    {
        var result = await Explorer().RunAsync("how does the Uploader upload a file");

        Assert.Equal(P("src/Uploader.cs"), result.Files[0].RelativePath);

        // Uploader calls RetryPolicy.Execute: the policy is listed for the name it supplies,
        // shown as that declaration and its body - not a window around a word.
        var policy = Assert.Single(result.Files, f => f.RelativePath == P("src/RetryPolicy.cs"));
        Assert.Matches("^defines (RetryPolicy|Execute), used by Uploader.cs$", policy.Reason);
        var range = Assert.Single(policy.Ranges);
        Assert.Contains("public static " + (policy.Reason.Contains("Execute") ? "void Execute" : "class RetryPolicy"), range.Code);
        Assert.False(policy.Confirmed);

        Assert.Contains(result.Files, f => f.RelativePath == P("src/Net/HttpGateway.cs") && f.Reason.Contains("HttpGateway"));
    }

    [Fact]
    public async Task The_users_of_a_type_join_it_as_related_source()
    {
        var result = await Explorer().RunAsync("RetryPolicy");

        Assert.Equal(P("src/RetryPolicy.cs"), result.Files[0].RelativePath);
        Assert.Contains(result.Files, f => f.RelativePath == P("src/SyncJob.cs"));
        Assert.Contains(result.Files, f => f.RelativePath == P("src/Uploader.cs"));
    }

    [Fact]
    public async Task A_word_the_code_spells_differently_still_finds_it()
    {
        var result = await Explorer().RunAsync("uploading");

        Assert.Equal(P("src/Uploader.cs"), result.Files[0].RelativePath);
    }

    [Fact]
    public async Task A_match_inside_a_method_shows_the_whole_method()
    {
        var result = await Explorer().RunAsync("attempt sleep");

        var policy = result.Files.First(f => f.RelativePath == P("src/RetryPolicy.cs"));
        var range = policy.Ranges[0];
        Assert.StartsWith("    public static void Execute(Action action)", range.Code);
        Assert.Contains("Thread.Sleep(Backoff(attempt));", range.Code);
        Assert.Contains(range.MatchLines, n => n > range.StartLine);
    }

    [Fact]
    public async Task Setup_questions_land_on_the_manifest()
    {
        var result = await Explorer().RunAsync("how do I run this project");

        Assert.Equal("package.json", result.Files[0].RelativePath);
        Assert.Contains("\"start\"", result.Files[0].Ranges[0].Code);
    }

    [Fact]
    public async Task A_setup_word_in_a_question_about_something_else_does_not_bury_its_matches()
    {
        Write("README.md", "# Demo\n\n## Running\n\nRun `npm start`, then open the running app in a browser.\n");

        var result = await Explorer().RunAsync("cancel a running upload");

        Assert.Equal(P("src/Uploader.cs"), result.Files[0].RelativePath);
    }

    [Fact]
    public async Task Words_that_together_name_an_identifier_find_its_declaration()
    {
        Write("src/StatusBar.cs", """
            public class StatusBar
            {
                public string BackgroundTaskProviderLabel => "Background tasks";
            }
            """);
        Write("src/Chatter.cs", """
            // background task: the provider sets the label
            // background task: the provider sets the label
            // background task: the provider sets the label
            public class Chatter { }
            """);

        var result = await Explorer().RunAsync("background task provider label");

        Assert.Equal(P("src/StatusBar.cs"), result.Files[0].RelativePath);
        Assert.DoesNotContain("backgroundtaskproviderlabel", result.Keywords);
    }

    [Fact]
    public async Task A_member_name_is_only_followed_to_a_file_whose_type_the_code_names()
    {
        Write("src/Runner.cs", """
            public class Runner
            {
                public void Wait(Process process)
                {
                    while (!process.HasExited) { process.WaitForExitCore(); }
                }
            }
            """);
        Write("src/Term/ConPty.cs", """
            public sealed class ConPty
            {
                public bool HasExited { get; private set; }
                public void WaitForExitCore() { }
            }
            """);

        var result = await Explorer().RunAsync("Runner wait");

        Assert.Equal(P("src/Runner.cs"), result.Files[0].RelativePath);
        Assert.DoesNotContain(result.Files, f => f.RelativePath == P("src/Term/ConPty.cs"));
    }

    [Fact]
    public async Task Prose_in_docs_is_not_followed_as_a_reference()
    {
        var result = await Explorer().RunAsync("retry docs");

        Assert.DoesNotContain(result.Files, f => f.Reason.EndsWith("retry.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_files_the_model_picks_lead_followed_by_their_related_source()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Backoff"), Pick("1"));

        var result = await Explorer(model).RunAsync("how does it recover from failed uploads");

        Assert.Equal(P("src/RetryPolicy.cs"), result.Files[0].RelativePath);
        Assert.True(result.Files[0].Confirmed);
        Assert.All(result.Files.Skip(1), f => Assert.False(f.Confirmed));
        Assert.All(result.Files.Skip(1), f => Assert.NotEmpty(f.Reason));
        Assert.True(result.IsFinal);
        Assert.Contains("RetryPolicy", result.Keywords);
    }

    [Fact]
    public async Task The_model_sees_the_real_file_list_and_numbered_candidates_with_their_lines()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Backoff"), Pick("1"));

        await Explorer(model).RunAsync("how does it back off");

        var (keywordPrompt, keywordTool) = model.Seen[0];
        Assert.Equal("search", keywordTool);
        Assert.Contains("RetryPolicy.cs", keywordPrompt);
        Assert.DoesNotContain("node_modules", keywordPrompt);

        var (judgePrompt, judgeTool) = model.Seen[1];
        Assert.Equal("pick", judgeTool);
        Assert.Contains("[1] src/RetryPolicy.cs (declares RetryPolicy)", judgePrompt);
        Assert.Contains("Backoff(int attempt)", judgePrompt);
        Assert.DoesNotContain("hunter2marker", judgePrompt);
    }

    [Fact]
    public async Task The_keyword_step_lists_the_files_by_folder_and_ends_with_the_question()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("1"));

        await Explorer(model).RunAsync("how does it back off");

        var prompt = model.Seen[0].Prompt;
        Assert.Contains("src: RetryPolicy.cs, SyncJob.cs, Uploader.cs", prompt);
        Assert.Contains("src/Net: HttpGateway.cs", prompt);
        Assert.Contains("tests: RetryPolicyTests.cs", prompt);
        Assert.EndsWith("Question: how does it back off", prompt);
    }

    [Fact]
    public async Task Types_hidden_in_a_file_of_another_name_are_listed_for_the_keyword_step()
    {
        Write("src/Helpers.cs", """
            public static class ToolCallParser { }
            internal sealed record ParsedCall(string Name);
            """);
        var model = new ScriptedModel(Keywords("ToolCallParser"), Pick("1"));

        await Explorer(model).RunAsync("where is the reply parsed into a call");

        var prompt = model.Seen[0].Prompt;
        Assert.Contains("src/Helpers.cs: ToolCallParser, ParsedCall", prompt);

        // A type named like its file is already in the file list; test fixtures are noise.
        Assert.DoesNotContain("RetryPolicy.cs: RetryPolicy", prompt);
        Assert.DoesNotContain("RetryPolicyTests.cs:", prompt);
    }

    [Fact]
    public async Task A_file_the_model_names_without_a_matching_line_is_judged_by_its_declarations()
    {
        var model = new ScriptedModel(Keywords("Levitate", "web/player.js"), Pick("1"));

        var result = await Explorer(model).RunAsync("where is the jump");

        var judge = model.Seen[1].Prompt;
        Assert.Contains("[1] web/player.js", judge);
        Assert.Contains("1: function onKeyDown(e) {", judge);
        Assert.Equal(P("web/player.js"), result.Files[0].RelativePath);
        Assert.True(result.Files[0].Confirmed);
    }

    [Fact]
    public async Task Rounds_stop_at_the_cap_and_still_return_a_guess_unconfirmed()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("", "Backoff"), Pick("", "velocityY"), Pick("", "never"));

        var result = await new SourceExplorer(_index, model) { MaxRounds = 2 }.RunAsync("anything");

        Assert.Equal(3, model.Seen.Count);
        Assert.True(result.IsFinal);
        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, f => Assert.False(f.Confirmed));
    }

    [Fact]
    public async Task A_round_whose_keywords_miss_still_offers_the_ranked_files_to_the_judge()
    {
        var model = new ScriptedModel(Keywords("NotThere anywhere"), Pick("1"));

        var result = await Explorer(model).RunAsync("how does it back off");

        Assert.Contains("[1] src/RetryPolicy.cs", model.Seen[1].Prompt);
        Assert.Equal(P("src/RetryPolicy.cs"), result.Files[0].RelativePath);
        Assert.True(result.Files[0].Confirmed);
    }

    [Fact]
    public async Task Ranges_mark_the_matching_lines()
    {
        var model = new ScriptedModel(Keywords("velocityY"), Pick("1"));

        var result = await Explorer(model).RunAsync("jump");

        var file = result.Files[0];
        Assert.Equal(P("web/player.js"), file.RelativePath);
        var range = Assert.Single(file.Ranges);
        Assert.Equal([3], range.MatchLines);
        Assert.Contains("velocityY = -12", range.Code);
    }

    [Fact]
    public async Task A_keyword_common_to_many_files_does_not_bring_them_all_in()
    {
        for (var i = 0; i < 25; i++)
        {
            Write($"src/Widget{i}.js", "let state = {};\nstate.ready = true;");
        }

        var model = new ScriptedModel(Keywords("state, velocityY"), Pick(""));

        var result = await Explorer(model).RunAsync("how does the player jump");

        Assert.Equal(P("web/player.js"), result.Files[0].RelativePath);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("Widget"));
    }

    [Fact]
    public async Task Short_keywords_only_match_at_the_start_of_a_word()
    {
        Write("src/Text.js", "function truncate(s) { return s.slice(0, 3); }");
        Write("src/Runner.js", "function run() { runGame(); }");

        var result = await Explorer().RunAsync("run");

        Assert.Contains(result.Files, f => f.RelativePath == P("src/Runner.js"));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == P("src/Text.js"));
    }

    /// <summary>An Electron app with no .gitignore and a node_modules full of "run".</summary>
    private static string ElectronProject()
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
            using var index = new SourceIndex(root, new SourceIndexOptions { Watch = false });
            var result = await new SourceExplorer(index, null).RunAsync("how to run the project");

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
            using var index = new SourceIndex(root, new SourceIndexOptions { Watch = false });
            var model = new ScriptedModel(Keywords("npmStart, launchApp"), Pick("1, 2"));

            var result = await new SourceExplorer(index, model).RunAsync("how do I start the app");

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
    public async Task A_path_the_model_names_reaches_the_judge_first()
    {
        var model = new ScriptedModel(Keywords("Send", "src/Net/HttpGateway.cs"), Pick("1"));

        var result = await Explorer(model).RunAsync("what does the network layer do");

        Assert.Equal(P("src/Net/HttpGateway.cs"), result.Files[0].RelativePath);
        Assert.StartsWith("[1] src/Net/HttpGateway.cs", model.Seen[1].Prompt.Split('\n')[3]);
    }

    [Fact]
    public async Task No_pick_retries_with_the_models_new_keywords()
    {
        var model = new ScriptedModel(Keywords("Backoff"), Pick("", "onKeyDown, velocityY"), Pick("1"));

        var result = await Explorer(model).RunAsync("what happens when space is pressed");

        Assert.Equal(P("web/player.js"), result.Files[0].RelativePath);
        Assert.Equal(2, result.Round);
        Assert.Equal(3, model.Seen.Count);

        // Files turned down in round one are not offered again.
        Assert.Contains("RetryPolicy.cs", model.Seen[1].Prompt);
        Assert.DoesNotContain("RetryPolicy.cs", model.Seen[2].Prompt);
    }

    [Fact]
    public async Task Provisional_results_arrive_before_the_model_answers()
    {
        var gate = new TaskCompletionSource();
        var model = new ScriptedModel(Keywords("RetryPolicy"), Pick("1")) { Gate = gate };
        var explorer = Explorer(model);
        var provisional = new TaskCompletionSource<DiscoveryResult>();
        explorer.Progress += (_, r) => provisional.TrySetResult(r);

        var run = explorer.RunAsync("retry policy");
        var first = await provisional.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(first.IsFinal);
        Assert.Equal(0, first.Round);
        Assert.NotEmpty(first.Files);

        gate.SetResult();
        Assert.True((await run).IsFinal);
    }

    [Fact]
    public async Task A_failing_model_degrades_to_the_ranked_results()
    {
        var model = new ScriptedModel { Throw = () => new HelperModelException("down") };

        var result = await Explorer(model).RunAsync("retry policy");

        Assert.True(result.IsFinal);
        Assert.Equal(P("src/RetryPolicy.cs"), result.Files[0].RelativePath);
    }

    [Fact]
    public async Task The_model_cannot_pick_everything()
    {
        var model = new ScriptedModel(Keywords("RetryPolicy, Execute, Send, Upload, Sync"), Pick("1,2,3,4,5,6"));

        var result = await Explorer(model).RunAsync("retries");

        Assert.Equal(4, result.Files.Count(f => f.Confirmed));
    }

    [Fact]
    public async Task Cancelling_stops_the_search()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var model = new ScriptedModel(Keywords("RetryPolicy")) { Gate = gate };

        var run = Explorer(model).RunAsync("retry", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData("retries", "retry")]
    [InlineData("uploading", "upload")]
    [InlineData("uploaders", "upload")]
    [InlineData("handled", "handl")]
    [InlineData("send", "send")]
    public void Stems_drop_plural_and_verb_endings(string word, string stem) =>
        Assert.Equal(stem, SourceExplorer.Stem(word));

    [Fact]
    public void A_declaration_range_takes_its_doc_comment_and_body()
    {
        string[] lines = ["using System;", "", "/// <summary>Retries.</summary>", "[Obsolete]", "public static class RetryPolicy", "{", "    void A() { }", "}", "class Next { }"];

        var range = SourceExplorer.DeclarationRange(lines, 5);

        Assert.Equal(3, range.StartLine);
        Assert.Equal(8, range.EndLine);
        Assert.Equal([5], range.MatchLines);
    }

    [Fact]
    public async Task A_match_in_a_doc_comment_starts_the_slice_at_that_comment()
    {
        var result = await Explorer().RunAsync("exponential");

        var range = result.Files.First(f => f.RelativePath == P("src/RetryPolicy.cs")).Ranges[0];
        Assert.StartsWith("/// <summary>Retries an action with exponential backoff.", range.Code);
    }

    [Fact]
    public void Block_end_follows_braces_or_indentation()
    {
        string[] csharp = ["void A()", "{", "    var s = \"}\";", "    B();", "}", "void C() { }"];
        Assert.Equal(5, SourceExplorer.BlockEnd(csharp, 1));
        Assert.Equal(6, SourceExplorer.BlockEnd(csharp, 6));

        string[] python = ["def a():", "    x = 1", "    return x", "", "def b():", "    pass"];
        Assert.Equal(3, SourceExplorer.BlockEnd(python, 1));
    }
}
