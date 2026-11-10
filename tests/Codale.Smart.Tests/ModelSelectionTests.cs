using System.Text;
using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class ModelSelectionTests
{
    [Fact]
    public void Settings_resolution_defaults_and_preserves_experimental_model_selection()
    {
        Assert.Same(SmartModels.Default, SmartModels.Resolve(null));
        Assert.Same(SmartModels.Default, SmartModels.Resolve(""));
        Assert.Same(SmartModels.Default, SmartModels.Resolve("unknown"));
        Assert.Same(SmartModels.Default, SmartModels.Resolve(SmartModels.Default.Id));
        Assert.False(SmartModels.Gemma4.Released);
        Assert.Same(SmartModels.Gemma4, SmartModels.Resolve(SmartModels.Gemma4.Id));
        Assert.Equal(2, SmartModels.All.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public async Task Changing_selection_while_disabled_does_not_install_or_load()
    {
        await using var smart = new SmartExperience(() => throw new Exception("Must not load"),
            (_, _, _) => throw new Exception("Must not download"));
        await smart.ConfigureAsync(false, SmartModels.Gemma4);
        Assert.Same(SmartModels.Gemma4, smart.Model);
        Assert.Equal(SmartState.Disabled, smart.Status.State);
    }

    [Fact]
    public async Task Rapid_switches_cancel_downloads_and_ignore_stale_progress_and_errors()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<double>? staleProgress = null;
        CancellationToken oldToken = default;
        await using var smart = new SmartExperience(() => new Runtime(), async (model, progress, ct) =>
        {
            if (model == SmartModels.Default)
            {
                staleProgress = progress; oldToken = ct; started.SetResult();
                await release.Task;
                throw new IOException("late download failure");
            }
            return model.Id;
        });
        var first = smart.ConfigureAsync(true, SmartModels.Default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = smart.ConfigureAsync(true, SmartModels.Gemma4);
        Assert.True(oldToken.IsCancellationRequested);
        var last = smart.ConfigureAsync(false, SmartModels.Gemma4);
        staleProgress!.Report(.8);
        Assert.Equal(SmartState.Disabled, smart.Status.State);
        release.SetResult();
        await Task.WhenAll(first, second, last).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SmartState.Disabled, smart.Status.State);
        await smart.ConfigureAsync(true, SmartModels.Gemma4);
        staleProgress.Report(.9);
        Assert.Equal(SmartState.Ready, smart.Status.State);
        Assert.Contains(SmartModels.Gemma4.Name, smart.Status.Message);
    }

    [Fact]
    public async Task Switching_cancels_inference_before_disposal_and_rejects_late_answers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool running = false;
        var firstRuntime = new Runtime
        {
            GenerateText = (_, ct) =>
            {
                running = true; started.SetResult();
                ct.WaitHandle.WaitOne();
                running = false;
                // Deliberately ignore cancellation at the native boundary.
                return "{\"intent\":\"act\"}";
            },
            OnDispose = () => Assert.False(running)
        };
        int factories = 0;
        await using var smart = new SmartExperience(() => factories++ == 0 ? firstRuntime : new Runtime(),
            (_, _, _) => Task.FromResult("model"));
        await smart.ConfigureAsync(true, SmartModels.Default);
        var request = smart.ClassifyAsync(new(Environment.CurrentDirectory, Environment.CurrentDirectory, "routing"), "fix it", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await smart.ConfigureAsync(true, SmartModels.Gemma4).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await request);
        Assert.True(firstRuntime.Disposed);
        Assert.Equal(SmartState.Ready, smart.Status.State);
        Assert.Same(SmartModels.Gemma4, smart.Model);
    }

    [Fact]
    public async Task Switching_during_native_load_disposes_the_old_runtime_before_loading_the_new_one()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadingToken = default;
        var oldRuntime = new Runtime
        {
            OnLoad = ct =>
            {
                loadingToken = ct; started.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        int factories = 0;
        await using var smart = new SmartExperience(() =>
        {
            if (factories++ == 0) return oldRuntime;
            Assert.True(oldRuntime.Disposed);
            return new Runtime();
        }, (_, _, _) => Task.FromResult("model"));
        var first = smart.ConfigureAsync(true, SmartModels.Default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = smart.ConfigureAsync(true, SmartModels.Gemma4);
        Assert.True(loadingToken.IsCancellationRequested);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(oldRuntime.Disposed);
        Assert.Equal(SmartState.Ready, smart.Status.State);
        Assert.Contains(SmartModels.Gemma4.Name, smart.Status.Message);
    }

    [Fact]
    public async Task Failed_selection_is_retained_and_retry_uses_the_same_model()
    {
        bool fail = true;
        var installed = new List<SmartModel>();
        await using var smart = new SmartExperience(() => new Runtime(), (model, _, _) =>
        { installed.Add(model); return fail ? Task.FromException<string>(new IOException("offline")) : Task.FromResult("model"); });
        await smart.ConfigureAsync(true, SmartModels.Gemma4);
        Assert.Equal(SmartState.Error, smart.Status.State);
        Assert.Contains(SmartModels.Gemma4.Name, smart.Status.Message);
        fail = false;
        await smart.SetEnabledAsync(true);
        Assert.Equal(SmartState.Ready, smart.Status.State);
        Assert.All(installed, m => Assert.Same(SmartModels.Gemma4, m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gemma_uses_tools_and_formats_final_output_for_existing_features(bool thinking)
    {
        var folder = Directory.CreateTempSubdirectory("gemma-roundtrip-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "a.cs"), "public class Sample {}");
            var runtime = new Runtime
            {
                GenerateText = (prompt, _) =>
                {
                    Assert.StartsWith("<bos><|turn>system", prompt);
                    if (prompt.Contains("Exploration is complete."))
                    {
                        Assert.DoesNotContain("<|think|>", prompt);
                        return prompt.Contains("Route the new message") ? "{\"intent\":\"act\"}"
                            : prompt.Contains("context selection") ? "[\"a\"]"
                            : prompt.Contains("Translate the user's request") ? "{\"command\":\"git status\"}"
                            : prompt.Contains("Write a Conventional Commit") ? "feat: add sample"
                            : prompt.Contains("Write a brief for a coding agent") ? "Task: Explain Sample.\nWhere:\n- a.cs:1-1 — Sample definition."
                            : "Sample summary.";
                    }
                    Assert.Contains("<|tool>declaration:read_file", prompt);
                    if (!prompt.Contains("<|tool_response>response:read_file")) return "<|tool_call>call:read_file{path:<|\"|>a.cs<|\"|>}<tool_call|>";
                    Assert.Contains("<|tool_response>response:read_file{", prompt);
                    Assert.Contains("public class Sample", prompt);
                    return "Sample summary.";
                }
            };
            await using var smart = new SmartExperience(() => runtime, (_, _, _) => Task.FromResult("model"));
            await smart.ConfigureAsync(true, SmartModels.Gemma4); smart.Thinking = thinking;
            SmartRequestContext Context(string task) => new(folder.FullName, folder.FullName, task);
            var summary = await smart.SummarizeAsync(Context("summary"), "a.cs");
            Assert.Equal("Sample summary.", summary?.Value);
            Assert.Single(summary!.Evidence);
            Assert.Equal(SmartIntent.Act, (await smart.ClassifyAsync(Context("routing"), "fix it", null))?.Value);
            Assert.Equal("a", (await smart.RankAsync(Context("ranking"), "sample", [new("a", "a.cs")]))?.Value?[0]);
            Assert.Equal("git status", (await smart.ShellCommandAsync(Context("shell"), "status", "powershell.exe"))?.Value);
            Assert.Equal("feat: add sample", (await smart.CommitMessageAsync(Context("commit")))?.Value);
            Assert.Contains("a.cs:1-1", (await smart.BriefAsync(Context("brief"), "Explain Sample", SmartIntent.Ask, "a.cs:1-1\npublic class Sample {}"))!.Value!);
            Assert.Same(SmartModels.Gemma4, runtime.Model);
        }
        finally { folder.Delete(true); }
    }

    private sealed class Runtime : ISmartRuntime
    {
        public Func<string, CancellationToken, string> GenerateText = (_, _) => "{\"intent\":\"act\"}";
        public Action? OnDispose;
        public Action<CancellationToken>? OnLoad;
        public SmartModel? Model;
        public bool Disposed;
        public string Backend => "CPU";
        public void Configure(SmartModel model) => Model = model;
        public void Load(string modelPath, bool gpu, CancellationToken ct) { ct.ThrowIfCancellationRequested(); OnLoad?.Invoke(ct); }
        public int CountTokens(string prompt) => Encoding.UTF8.GetByteCount(prompt) / 4;
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null) => GenerateText(prompt, ct);
        public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
    }
}
