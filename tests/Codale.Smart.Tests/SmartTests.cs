using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class SmartTests
{
    [Fact]
    public async Task Commit_generation_passes_a_format_grammar_and_preserves_the_message()
    {
        var runtime = new CommitRuntime();
        await using var smart = new SmartExperience(() => runtime, (_, _) => Task.FromResult("model"));
        await smart.SetEnabledAsync(true);
        var message = await smart.CommitMessageAsync("diff --git a/theme.cs b/theme.cs\n+DarkMode");
        Assert.NotNull(runtime.Grammar);
        Assert.DoesNotContain("body", runtime.Grammar);
        Assert.Equal("feat(theme): add dark mode", message);
    }

    private sealed class CommitRuntime : ISmartRuntime
    {
        public string? Grammar;
        public string Backend => "test";
        public void Load(string modelPath, bool gpu, CancellationToken ct) { }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null)
        { Grammar = grammar; return "feat(theme): add dark mode"; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("feat(git): generate a message\n\nExplain the change.\n\nRefs: #12", false)]
    [InlineData("feat(api)!: switch authentication endpoint from /login to /v2/auth", true)]
    [InlineData("fix: correct behavior\radditional line", false)]
    [InlineData("fix!: change behavior", true)]
    [InlineData("Generate a message", false)]
    [InlineData("feat: ", false)]
    public void Commit_messages_require_a_single_conventional_subject(string message, bool valid)
    {
        Assert.Equal(valid ? message : null, SmartExperience.ParseCommitMessage(message));
    }

    [Theory]
    [InlineData("{\"intent\":\"ask\"}", SmartIntent.Ask)]
    [InlineData("{\"intent\":\"plan\"}", SmartIntent.Plan)]
    [InlineData("{\"intent\":\"act\"}", SmartIntent.Act)]
    [InlineData("{\"intent\":\"delete\"}", null)]
    [InlineData("{\"intent\":42}", null)]
    [InlineData("not json", null)]
    public void Intent_output_is_strict(string text, SmartIntent? expected) => Assert.Equal(expected, SmartExperience.ParseIntent(text));

    [Fact]
    public void Rankings_reject_unknown_duplicate_or_missing_ids()
    {
        SmartCandidate[] candidates = [new("a", "a.cs", "a"), new("b", "b.cs", "b")];
        Assert.Equal(new[] { "b", "a" }, SmartExperience.ParseRanking("[\"b\",\"a\"]", candidates));
        Assert.Null(SmartExperience.ParseRanking("[\"a\",\"a\"]", candidates));
        Assert.Null(SmartExperience.ParseRanking("[\"a\",\"c\"]", candidates));
        Assert.Null(SmartExperience.ParseRanking("[\"a\"]", candidates));
    }

    [Fact]
    public async Task Gpu_failure_falls_back_to_cpu_and_disable_releases_it()
    {
        var attempts = new List<bool>(); var runtimes = new List<FakeRuntime>();
        await using var smart = new SmartExperience(() => { var r = new FakeRuntime(attempts); runtimes.Add(r); return r; }, (_, _) => Task.FromResult("model"));
        await smart.SetEnabledAsync(true);
        Assert.Equal(new[] { true, false }, attempts);
        Assert.Equal("CPU", smart.Status.Backend);
        Assert.Equal(SmartIntent.Plan, await smart.ClassifyAsync("plan", null));
        await smart.SetEnabledAsync(false);
        Assert.All(runtimes, r => Assert.True(r.Disposed));
        Assert.Null(await smart.ClassifyAsync("plan", null));
    }

    [Fact]
    public async Task Cpu_failure_is_reported_without_throwing()
    {
        await using var smart = new SmartExperience(() => new FakeRuntime([], true), (_, _) => Task.FromResult("model"));
        await smart.SetEnabledAsync(true);
        Assert.Equal(SmartState.Error, smart.Status.State);
    }

    [Fact]
    public async Task Disable_cancels_loading_and_cannot_publish_ready_afterwards()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var smart = new SmartExperience(() => new FakeRuntime([]), async (_, ct) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "model"; });
        var enable = smart.SetEnabledAsync(true);
        await started.Task;
        await smart.SetEnabledAsync(false);
        await enable;
        Assert.Equal(SmartState.Disabled, smart.Status.State);
    }

    [Fact]
    public void Summary_cache_rejects_changed_source_and_isolates_worktrees()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codale-smart-test-" + Guid.NewGuid());
        try
        {
            var cache = new SummaryCache(folder);
            cache.Put("tree-a", "file.cs", "hash-a", "summary");
            Assert.Equal("summary", cache.Get("tree-a", "file.cs", "hash-a"));
            Assert.Null(cache.Get("tree-a", "file.cs", "hash-b"));
            Assert.Null(cache.Get("tree-b", "file.cs", "hash-a"));
            cache.Prune("tree-a", new HashSet<string>());
            Assert.Null(cache.Get("tree-a", "file.cs", "hash-a"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Interactive_request_preempts_a_running_summary()
    {
        var runtime = new BlockingRuntime();
        await using var smart = new SmartExperience(() => runtime, (_, _) => Task.FromResult("model"));
        await smart.SetEnabledAsync(true);
        var background = smart.SummarizeAsync("file.cs", "source");
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var intent = await smart.ClassifyAsync("plan this", null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SmartIntent.Plan, intent);
        Assert.Null(await background);
        Assert.Equal(1, runtime.MaxActive);
    }

    private sealed class BlockingRuntime : ISmartRuntime
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public int MaxActive;
        public string Backend => "GPU";
        public void Load(string modelPath, bool gpu, CancellationToken ct) { }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null)
        {
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                if (prompt.Contains("Summarize this file"))
                {
                    Started.SetResult();
                    ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested();
                }
                return "{\"intent\":\"plan\"}";
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { }
    }

    private sealed class FakeRuntime(List<bool> attempts, bool failCpu = false) : ISmartRuntime
    {
        public bool Disposed;
        public string Backend => "CPU";
        public void Load(string modelPath, bool gpu, CancellationToken ct)
        { attempts.Add(gpu); if (gpu || failCpu) throw new InvalidOperationException("Unavailable"); }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null) => "{\"intent\":\"plan\"}";
        public void Dispose() => Disposed = true;
    }
}
