namespace Codale.Smart.Tests;

public sealed class SmartTaskRunnersTests
{
    private sealed class FakeHost : ISmartTaskHost
    {
        public bool Enabled { get; set; } = true;
        public bool Ready { get; set; } = true;
        public string? Answer { get; set; }
        public bool Hang { get; set; }
        public List<(SmartRequestContext Context, int Cap, bool Background)> Calls { get; } = [];

        public bool IsFeatureEnabled(string task) => Enabled;

        public bool IsReady => Ready;

        public async Task<SmartResult<T>?> GenerateAsync<T>(SmartRequestContext context, string system, string input, int cap,
            Func<string, T?> parse, bool background, CancellationToken ct) where T : class
        {
            Calls.Add((context, cap, background));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Answer is null ? null : new SmartResult<T>(parse(Answer), [], "activity");
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AHelperIsAvailableOnlyWhenItsFeatureIsOnAndTheModelIsReady(bool enabled, bool ready, bool expected)
    {
        var host = new FakeHost { Enabled = enabled, Ready = ready };

        Assert.Equal(expected, SmartHelperRunner.IsAvailable(host, "routing"));
    }

    [Fact]
    public async Task AnUnavailableHelperAnswersNothingAndCallsNoModel()
    {
        var host = new FakeHost { Enabled = false, Answer = "x" };

        var result = await SmartHelperRunner.RunAsync(host, "/project", "routing", TimeSpan.FromSeconds(5),
            "system", "input", 64, text => text, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task AHelperAnswerCarriesTheProjectTaskAndCap()
    {
        var host = new FakeHost { Answer = "keywords" };

        var result = await SmartHelperRunner.RunAsync(host, "/project", "keywords", TimeSpan.FromSeconds(5),
            "system", "input", 128, text => text, CancellationToken.None);

        Assert.Equal("keywords", result);
        var (context, cap, background) = Assert.Single(host.Calls);
        Assert.Equal(("/project", "keywords", 128, false), (context.Project, context.Task, cap, background));
    }

    [Fact]
    public async Task AHelperWithNoAnswerGivesNull()
    {
        var host = new FakeHost { Answer = null };

        var result = await SmartHelperRunner.RunAsync(host, "/project", "routing", TimeSpan.FromSeconds(5),
            "system", "input", 64, text => text, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AHelperThatRunsPastItsDeadlineGivesNull()
    {
        var host = new FakeHost { Hang = true };

        var result = await SmartHelperRunner.RunAsync(host, "/project", "routing", TimeSpan.FromMilliseconds(20),
            "system", "input", 64, text => text, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AnInlineSuggestionStripsTheRepeatedPrefix()
    {
        var host = new FakeHost { Answer = "Get-ChildItem -File" };

        var suggestion = await SmartInlineSuggester.SuggestAsync(host, "complete", "/project", "Get-ChildI",
            TimeSpan.Zero, 32, CancellationToken.None);

        Assert.Equal("tem -File", suggestion);
        Assert.True(Assert.Single(host.Calls).Background);
    }

    [Fact]
    public async Task AnUnavailableInlineSuggestionCallsNoModel()
    {
        var host = new FakeHost { Ready = false, Answer = "x" };

        var suggestion = await SmartInlineSuggester.SuggestAsync(host, "complete", "/project", "ls",
            TimeSpan.Zero, 32, CancellationToken.None);

        Assert.Null(suggestion);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ASupersededInlineSuggestionGivesNull()
    {
        var host = new FakeHost { Answer = "x" };
        using var superseded = new CancellationTokenSource();
        superseded.Cancel();

        var suggestion = await SmartInlineSuggester.SuggestAsync(host, "complete", "/project", "ls",
            TimeSpan.FromSeconds(1), 32, superseded.Token);

        Assert.Null(suggestion);
    }
}