using System.Diagnostics;

namespace Codale.Smart.Tests;

/// <summary>How each task uses Granite 4.2: reasoning level, sampling, grammar, stops, and the commit and FIM inputs.</summary>
public sealed class GranitePolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("smart-granite-policy-").FullName;
    private SmartRequestContext Context(string task) => new(_root, _root, task);

    private sealed record Call(string Prompt, int MaxTokens, string? Grammar, SmartGenerateOptions Options);
    private sealed class Runtime(Func<Call, string> answer) : ISmartRuntime
    {
        public readonly List<Call> Calls = [];
        public Func<Call, CancellationToken, string>? Blocking;
        public string Backend => "test";
        public void Load(string modelPath, bool gpu, CancellationToken ct) { }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null) =>
            Generate(prompt, maxTokens, ct, grammar, SmartGenerateOptions.Default);
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar, SmartGenerateOptions options)
        {
            var call = new Call(prompt, maxTokens, grammar, options);
            lock (Calls) Calls.Add(call);
            return options.ApplyStops(Blocking?.Invoke(call, ct) ?? answer(call));
        }
        public void Dispose() { }
    }
    private static async Task<SmartExperience> Ready(Runtime runtime, bool thinking = false)
    {
        var smart = new SmartExperience(() => runtime, (_, _) => Task.FromResult("model")) { Thinking = thinking };
        await smart.SetEnabledAsync(true);
        return smart;
    }

    private void Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _root, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
    private void StageChange()
    {
        Git("init", "--quiet");
        Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "Theme.cs"), "public class Theme { public bool DarkMode { get; set; } }\n");
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{\"lockfileVersion\": 3, \"packages\": {}}\n");
        Git("add", ".");
    }

    [Theory]
    [InlineData("routing", false, SmartReasoning.Off)]
    [InlineData("routing", true, SmartReasoning.Off)]
    [InlineData("shell", true, SmartReasoning.Off)]
    [InlineData("completion", true, SmartReasoning.Off)]
    [InlineData("assess", true, SmartReasoning.Off)]
    [InlineData("commit", false, SmartReasoning.Low)]
    [InlineData("commit", true, SmartReasoning.Low)]
    [InlineData("summary", true, SmartReasoning.Full)]
    [InlineData("context", true, SmartReasoning.Full)]
    [InlineData("brief", false, SmartReasoning.Off)]
    public void Reasoning_follows_the_task(string task, bool thinking, SmartReasoning expected) =>
        Assert.Equal(expected, SmartExperience.ReasoningFor(task, thinking));

    [Fact]
    public async Task Every_generation_samples_with_the_recommended_settings_and_the_pinned_seed()
    {
        var runtime = new Runtime(c => c.Grammar?.Contains("intent") == true ? "{\"intent\":\"ask\"}" : "{\"command\":\"git status --short\"}");
        await using var smart = await Ready(runtime);
        smart.Seed = 42;
        await smart.ClassifyAsync(Context("routing"), "how does it work?", null);
        await smart.ShellCommandAsync(Context("shell"), "show git status", "pwsh.exe");
        Assert.Equal(2, runtime.Calls.Count);
        Assert.All(runtime.Calls, c => { Assert.Equal(1.0f, c.Options.Temperature); Assert.Equal(0.95f, c.Options.TopP); Assert.Equal(42u, c.Options.Seed); });
    }

    [Fact]
    public async Task Routing_and_shell_never_reason_even_when_thinking_is_on()
    {
        var runtime = new Runtime(c => c.Grammar?.Contains("intent") == true ? "{\"intent\":\"act\"}" : "{\"command\":\"dir /a\"}");
        await using var smart = await Ready(runtime, thinking: true);
        Assert.Equal(SmartIntent.Act, (await smart.ClassifyAsync(Context("routing"), "fix the header", null))?.Value);
        Assert.Equal("dir /a", (await smart.ShellCommandAsync(Context("shell"), "list all files", "cmd.exe"))?.Value);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.All(runtime.Calls, c =>
        {
            Assert.EndsWith("<|im_start|>assistant\n<think></think>", c.Prompt);
            Assert.DoesNotContain(SmartConversation.LowEffortMarker, c.Prompt);
            Assert.DoesNotContain("<tools>", c.Prompt);
            Assert.NotNull(c.Grammar);
        });
    }

    [Fact]
    public async Task Low_effort_reasoning_runs_before_a_constrained_commit_message()
    {
        StageChange();
        var runtime = new Runtime(c => c.Grammar is null ? "Adds a DarkMode property: a new capability." : "feat(theme): add dark mode setting");
        await using var smart = await Ready(runtime);
        Assert.Equal("feat(theme): add dark mode setting", (await smart.CommitMessageAsync(Context("commit")))?.Value);
        Assert.Equal(2, runtime.Calls.Count);
        var (thought, answer) = (runtime.Calls[0], runtime.Calls[1]);
        Assert.Null(thought.Grammar);
        Assert.Equal(["</think>"], thought.Options.Stop);
        Assert.Equal(512, thought.MaxTokens);
        Assert.EndsWith(SmartConversation.LowEffortMarker + "<|im_end|>\n<|im_start|>assistant\n<think>\n", thought.Prompt);
        Assert.Contains("+public class Theme { public bool DarkMode { get; set; } }", thought.Prompt);
        Assert.StartsWith(thought.Prompt, answer.Prompt);
        Assert.EndsWith("Adds a DarkMode property: a new capability.\n</think>\n\n", answer.Prompt);
        Assert.Equal(SmartCommit.Grammar, answer.Grammar);
    }

    [Fact]
    public async Task Command_risk_never_reasons_within_its_eight_second_budget()
    {
        var runtime = new Runtime(_ => "{\"description\":\"Deletes a folder\",\"risk\":\"danger\",\"reason\":\"Removes files permanently\"}");
        await using var smart = await Ready(runtime, thinking: true);
        Assert.Equal("danger", (await smart.AssessCommandAsync(Context("assess"), "Remove-Item -Recurse C:\\data", "caution"))?.Value?.Risk);
        var call = Assert.Single(runtime.Calls);
        Assert.EndsWith("<think></think>", call.Prompt);
        Assert.Contains("char{1,130}", call.Grammar);
    }

    [Fact]
    public async Task Commit_messages_are_written_from_the_injected_staged_diff_without_tools()
    {
        StageChange();
        var runtime = new Runtime(_ => "feat(theme): add dark mode setting");
        await using var smart = await Ready(runtime);
        var result = await smart.CommitMessageAsync(Context("commit"));
        Assert.Equal("feat(theme): add dark mode setting", result?.Value);
        var call = runtime.Calls[^1];
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Contains("Staged files:", call.Prompt);
        Assert.Contains("+public class Theme { public bool DarkMode { get; set; } }", call.Prompt);
        Assert.Contains("[lock or generated file: diff omitted]", call.Prompt);
        Assert.DoesNotContain("lockfileVersion", call.Prompt);
        Assert.DoesNotContain("<tools>", call.Prompt);
        Assert.Equal(SmartCommit.Grammar, call.Grammar);
        Assert.Contains(result!.Evidence, e => e.Kind == "command" && e.Command!.StartsWith("git diff --cached", StringComparison.Ordinal));
        Assert.Empty(smart.Activities(_root)[0].Tools ?? []);
    }

    [Fact]
    public async Task Nothing_staged_means_no_commit_message_and_no_generation()
    {
        Git("init", "--quiet");
        var runtime = new Runtime(_ => "feat: unused");
        await using var smart = await Ready(runtime);
        Assert.Null(await smart.CommitMessageAsync(Context("commit")));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void Commit_input_keeps_small_files_whole_and_shortens_large_ones()
    {
        string Section(string name, int lines) => $"diff --git a/{name} b/{name}\n--- a/{name}\n+++ b/{name}\n@@ -1 +1 @@\n" +
            string.Concat(Enumerable.Range(0, lines).Select(i => $"+line {i} of {name}\n"));
        var patch = Section("small.cs", 3) + Section("big.cs", 2000) + Section("yarn.lock", 500);
        var input = SmartCommit.BuildInput(" small.cs | 3 +\n big.cs | 2000 +\n", patch, budget: 4000);
        Assert.Contains("+line 2 of small.cs", input);
        Assert.Contains("+line 0 of big.cs", input);
        Assert.DoesNotContain("+line 1999 of big.cs", input);
        Assert.Contains("more lines of this file omitted]", input);
        Assert.Contains("diff --git a/yarn.lock b/yarn.lock\n[lock or generated file: diff omitted]", input);
        Assert.True(input.Length < 4000 + 200);
        Assert.StartsWith("Staged files:\n small.cs | 3 +", input);
    }

    [Theory]
    [InlineData("feat(theme): add dark mode", true)]
    [InlineData("fix: x", true)]
    public void The_commit_grammar_bounds_the_subject(string message, bool valid)
    {
        Assert.Contains("subject ::= [!-~] [ -~]{0,79}", SmartCommit.Grammar);
        Assert.Equal(valid ? message : null, SmartExperience.ParseCommitMessage(message));
    }

    [Theory]
    [InlineData("feat: add math helpers git helpers theme classes adds public class MathHelpers with Add method,+",
        "feat: add math helpers git helpers theme classes adds public class MathHelpers with Add")]
    [InlineData("fix(pkg): update Serilog version from 3.1.1 to 4.0.0 to address compatibility and also something elsewh",
        "fix(pkg): update Serilog version from 3.1.1 to 4.0.0 to address compatibility and also something")]
    [InlineData("feat(csv-exporter): add sealed CsvExporter class that exports rows as newline-separated comma-separ",
        "feat(csv-exporter): add sealed CsvExporter class that exports rows as newline-separated")]
    [InlineData("fix(paginator): correct off-by-one boundary check in Page method that previously used '>' when",
        "fix(paginator): correct off-by-one boundary check in Page method that previously used '>'")]
    [InlineData("docs: add build instructions.", "docs: add build instructions")]
    [InlineData("refactor(order): replace manual loop with built-in Sum method", "refactor(order): replace manual loop with built-in Sum method")]
    [InlineData("feat(api)!: switch auth endpoint", "feat(api)!: switch auth endpoint")]
    public void Capped_or_dangling_subjects_are_tidied(string raw, string expected) => Assert.Equal(expected, SmartCommit.Tidy(raw));

    [Fact]
    public void The_fim_prompt_uses_granite_fim_tokens()
    {
        Assert.Equal("<|filename|>src/a.cs\n<|fim_prefix|>int x = <|fim_suffix|>;\n<|fim_middle|>", SmartExperience.FimPrompt("int x = ", ";\n", "src\\a.cs"));
        Assert.Equal("<|fim_prefix|>a< |b<|fim_suffix|><|fim_middle|>", SmartExperience.FimPrompt("a<|b", "", null));
    }

    [Fact]
    public async Task Fill_in_the_middle_stops_at_the_line_end_and_redraws_rejected_proposals()
    {
        int draws = 0;
        var runtime = new Runtime(_ => ++draws == 1 ? "a + b;\nreturn 0;" : "a - b;\n");
        await using var smart = await Ready(runtime);
        Assert.Equal("a + b;", await smart.FillInMiddleAsync(Context("completion"), "int sum = ", "\n}", "a.cs"));
        var call = runtime.Calls[0];
        Assert.StartsWith("<|filename|>a.cs\n<|fim_prefix|>", call.Prompt);
        Assert.Equal(SmartExperience.FimTokens, call.MaxTokens);
        Assert.Equal(["\n"], call.Options.Stop);
        Assert.Null(call.Grammar);
        draws = 0;
        Assert.Equal("a - b;", await smart.FillInMiddleAsync(Context("completion"), "int sum = ", "\n}", "a.cs", rejected: ["a + b;"]));
    }

    [Fact]
    public async Task Fill_in_the_middle_never_waits_for_interactive_work_but_preempts_summaries()
    {
        var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime(c => c.Prompt.Contains("<|fim_prefix|>") ? "x" : "{\"intent\":\"ask\"}")
        {
            Blocking = (c, ct) =>
            {
                if (c.Prompt.Contains("<|fim_prefix|>")) return "x";
                started.TrySetResult();
                WaitHandle.WaitAny([release.WaitHandle, ct.WaitHandle]);
                ct.ThrowIfCancellationRequested();
                return c.Prompt.Contains("Summarize") ? "Summary." : "{\"intent\":\"ask\"}";
            }
        };
        await using var smart = await Ready(runtime);
        var routing = smart.ClassifyAsync(Context("routing"), "hold", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await smart.FillInMiddleAsync(Context("completion"), "a", "b", null).WaitAsync(TimeSpan.FromSeconds(2)));
        release.Set();
        await routing;

        release.Reset();
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = smart.SummarizeAsync(Context("summary"), "file.cs");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("x", await smart.FillInMiddleAsync(Context("completion"), "a", "b", null).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(await summary);
    }

    [Theory]
    [InlineData("\n  foo();\nbar", "\n  foo();")]
    [InlineData("foo</think>bar", "foo")]
    [InlineData("no stop", "no stop")]
    public void Stops_apply_after_visible_text(string text, string expected) =>
        Assert.Equal(expected, new SmartGenerateOptions(Stop: ["\n", "</think>"]).ApplyStops(text));

    public void Dispose()
    {
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        Directory.Delete(_root, true);
    }
}
