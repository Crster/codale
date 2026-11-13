using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class RemoteExperienceTests
{
    [Fact]
    public void Summaries_are_invalidated_when_provider_or_model_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "codale-summary-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new SummaryCache(root, "provider-a:model-a");
            original.Put(root, "file.cs", "hash", "Original summary.");
            Assert.Equal("Original summary.", original.Get(root, "file.cs", "hash"));
            Assert.Null(new SummaryCache(root, "provider-b:model-b").Get(root, "file.cs", "hash"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static SmartExperience Create() => new(
        () => throw new InvalidOperationException("Native runtime must not be created."),
        (_, _) => throw new InvalidOperationException("Models must not be downloaded."));
    private static SmartRequestContext Context() => new(Environment.CurrentDirectory, Environment.CurrentDirectory, "caller-label");

    [Fact]
    public async Task Completion_preserves_whitespace_through_the_full_remote_pipeline()
    {
        await using var smart = Create();
        await smart.ConfigureRemoteAsync("test", (_, _, _, _, _) => Task.FromResult("\n    return true;  "));
        Assert.Equal("\n    return true;  ", await smart.FillInMiddleAsync(Context(), "", "}", "a.cs"));
    }

    [Fact]
    public async Task Deadline_failure_is_published_as_error_and_a_retry_can_succeed()
    {
        await using var smart = Create();
        var updates = new List<SmartActivity>();
        smart.ActivityUpdated += updates.Add;
        await smart.ConfigureRemoteAsync("test", async (_, _, _, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        Assert.Null(await smart.ClassifyAsync(Context() with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(100) }, "fix", null));
        Assert.Equal("error", updates.Last().State);
        Assert.Contains("timed out", updates.Last().Error);
        await smart.ConfigureRemoteAsync("test", (_, _, _, _, _) => Task.FromResult("{\"intent\":\"act\"}"));
        Assert.Equal(SmartIntent.Act, (await smart.ClassifyAsync(Context(), "fix", null))?.Value);
        Assert.Equal("completed", updates.Last().State);
    }

    [Fact]
    public async Task Remote_features_select_their_own_models_and_validate_results()
    {
        await using var smart = Create();
        var features = new List<string>();
        await smart.ConfigureRemoteAsync("test", (feature, system, input, cap, ct) =>
        {
            features.Add(feature);
            return Task.FromResult(feature switch
            {
                "routing" => "{\"intent\":\"act\"}",
                "shell" => "{\"command\":\"git status --short\"}",
                "ranking" => "[\"b\",\"a\"]",
                "completion" => "return true;",
                _ => "invalid"
            });
        });
        Assert.Equal(SmartIntent.Act, (await smart.ClassifyAsync(Context(), "fix it", null))?.Value);
        Assert.Equal("git status --short", (await smart.ShellCommandAsync(Context(), "show git status", "pwsh"))?.Value);
        Assert.Equal(new[] { "b", "a" }, (await smart.RankAsync(Context(), "query", [new("a", "a.cs"), new("b", "b.cs")]))?.Value);
        Assert.Equal("return true;", await smart.FillInMiddleAsync(Context(), "", "", "a.cs"));
        Assert.Equal(new[] { "routing", "shell", "ranking", "completion" }, features);
        await smart.ConfigureRemoteAsync("test", (_, _, _, _, _) => Task.FromResult("{\"intent\":\"invented\"}"));
        Assert.Null(await smart.ClassifyAsync(Context(), "fix it", null));
        Assert.Contains(smart.Activities(Environment.CurrentDirectory), a => a.State == "error");
    }

    [Fact]
    public async Task Off_features_make_no_request_and_switching_off_cancels_inflight_work()
    {
        await using var smart = Create();
        int calls = 0;
        await smart.ConfigureRemoteAsync("test", (_, _, _, _, _) => { calls++; return Task.FromResult("unused"); },
            featureEnabled: _ => false);
        Assert.Null(await smart.ShellCommandAsync(Context(), "list files", "pwsh"));
        Assert.Equal(0, calls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await smart.ConfigureRemoteAsync("test", async (_, _, _, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        var running = smart.ClassifyAsync(Context(), "fix", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await smart.ConfigureRemoteAsync("off", null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await running);
        Assert.Equal(SmartState.Disabled, smart.Status.State);
        Assert.Null(await smart.ClassifyAsync(Context(), "fix", null));
    }

    [Fact]
    public async Task Remote_summary_reads_a_real_file_and_records_fresh_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "codale-remote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.cs"), "public class Sample { }");
            await using var smart = Create();
            int calls = 0;
            await smart.ConfigureRemoteAsync("test", (feature, system, input, cap, ct) =>
            {
                Assert.Equal("summary", feature);
                calls++;
                if (calls == 1) return Task.FromResult("<tool_call><function=read_file><parameter=path>sample.cs</parameter></function></tool_call>");
                Assert.Contains("public class Sample", input);
                return Task.FromResult("Defines the Sample class.");
            });
            var result = await smart.SummarizeAsync(new(root, root, "summary"), "sample.cs");
            Assert.Equal("Defines the Sample class.", result?.Value);
            var evidence = Assert.Single(result!.Evidence);
            Assert.Equal("file", evidence.Kind);
            Assert.True(await evidence.IsFreshAsync());
            Assert.Equal("completed", Assert.Single(smart.Activities(root)).State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_provider_errors_are_recorded()
    {
        await using var smart = Create();
        await smart.ConfigureRemoteAsync("test", (_, _, _, _, _) => throw new InvalidOperationException("Endpoint unavailable"));
        Assert.Null(await smart.ClassifyAsync(Context(), "fix", null));
        Assert.Contains(smart.Activities(Environment.CurrentDirectory), a => a.Error == "Endpoint unavailable");
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => smart.ClassifyAsync(Context(), "fix", null, ct.Token));
    }
}
