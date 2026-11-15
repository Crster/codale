using Codale.Core.Agents;
using Codale.Core.Features;

namespace Codale.Agents.Tests;

public sealed class SmartServicesTests
{
    private sealed class RecordingStatus : ISmartRequestStatus
    {
        public List<(string Kind, string Task, string Outcome, string? Error)> Events { get; } = [];
        public void Start(string id, string task) => Events.Add(("start", task, "", null));
        public void Finish(string id, string outcome, string? error = null) => Events.Add(("finish", "", outcome, error));
    }

    private sealed class FakeClient : ISmartProviderClient
    {
        public string Role => "embedding";
        public bool IsEnabled => true;
        public string? ConfigurationError => null;
        public string EmbeddingIdentity { get; set; } = "off";
        public string FeatureIdentity => "fake";
        public string ConfigurationKey => "fake";

        public Task<string> CompleteAsync(string task, string system, string input, int tokens, CancellationToken ct) =>
            Task.FromResult("unused");
        public float[] Vector { get; set; } = [1f, 2f];
        public Exception? Fail { get; set; }
        public bool Hang { get; set; }
        public int DecideCalls { get; private set; }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Fail is not null ? throw Fail : Vector;
        }

        public Task<IReadOnlyDictionary<string, SmartDecision>> DecideAsync(object state,
            IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct)
        {
            DecideCalls++;
            return Fail is not null
                ? throw Fail
                : Task.FromResult<IReadOnlyDictionary<string, SmartDecision>>(new Dictionary<string, SmartDecision>());
        }
    }

    private static readonly IReadOnlyDictionary<string, SmartQuestion> NoQuestions = new Dictionary<string, SmartQuestion>();

    [Fact]
    public async Task ADecisionWithNoDeciderGivesNullAndCallsNothing()
    {
        var status = new RecordingStatus();

        var answers = await new SmartDecisionService(status).AskAsync(null, new object(), NoQuestions, CancellationToken.None);

        Assert.Null(answers);
        Assert.Empty(status.Events);
    }

    [Fact]
    public async Task ADecisionIsReportedAsCompleted()
    {
        var status = new RecordingStatus();
        var decider = new FakeClient();

        var answers = await new SmartDecisionService(status).AskAsync(decider, new object(), NoQuestions, CancellationToken.None);

        Assert.NotNull(answers);
        Assert.Equal(1, decider.DecideCalls);
        Assert.Equal(["start", "finish"], status.Events.Select(e => e.Kind));
        Assert.Equal("completed", status.Events[^1].Outcome);
    }

    [Fact]
    public async Task AFailedDecisionIsReportedWithItsMessageAndGivesNull()
    {
        var status = new RecordingStatus();
        var decider = new FakeClient { Fail = new InvalidOperationException("provider down") };

        var answers = await new SmartDecisionService(status).AskAsync(decider, new object(), NoQuestions, CancellationToken.None);

        Assert.Null(answers);
        Assert.Equal(("finish", "", "error", (string?)"provider down"), status.Events[^1]);
    }

    [Fact]
    public void AnEmbeddingWithTheModelOffUsesTheHashedEmbedderAndReportsNothing()
    {
        var status = new RecordingStatus();
        var service = new SmartEmbeddingService(status);
        var client = new FakeClient { EmbeddingIdentity = "off" };

        var vector = service.Embed(client, "hello");

        Assert.Equal(HashedTextEmbedder.Instance.Embed("hello"), vector);
        Assert.Empty(status.Events);
        Assert.False(SmartEmbeddingService.UsesModel(client));
        Assert.Equal(HashedTextEmbedder.Instance.Model, SmartEmbeddingService.ModelOf(client));
    }

    [Fact]
    public void AnEmbeddingWithAModelIsReportedAsCompleted()
    {
        var status = new RecordingStatus();
        var service = new SmartEmbeddingService(status);
        var client = new FakeClient { EmbeddingIdentity = "openai:key:url:model", Vector = [0.5f] };

        var vector = service.Embed(client, "hello");

        Assert.Equal([0.5f], vector);
        Assert.Equal(["start", "finish"], status.Events.Select(e => e.Kind));
        Assert.Equal(("finish", "", "completed", (string?)null), status.Events[^1]);
        Assert.Equal("openai:key:url:model", SmartEmbeddingService.ModelOf(client));
    }

    [Fact]
    public void AnEmbeddingThatRunsPastItsTimeoutIsReportedAndRethrown()
    {
        var status = new RecordingStatus();
        var service = new SmartEmbeddingService(status, TimeSpan.FromMilliseconds(20));
        var client = new FakeClient { EmbeddingIdentity = "openai:key:url:model", Hang = true };

        Assert.ThrowsAny<OperationCanceledException>(() => service.Embed(client, "hello"));

        Assert.Equal(("finish", "", "error", (string?)"The embedding request timed out. Check the connection and model."), status.Events[^1]);
    }

    [Fact]
    public void AnEmbeddingErrorIsReportedWithItsMessageAndRethrown()
    {
        var status = new RecordingStatus();
        var service = new SmartEmbeddingService(status);
        var client = new FakeClient { EmbeddingIdentity = "openai:key:url:model", Fail = new InvalidOperationException("bad key") };

        var error = Assert.Throws<InvalidOperationException>(() => service.Embed(client, "hello"));

        Assert.Equal("bad key", error.Message);
        Assert.Equal(("finish", "", "error", (string?)"bad key"), status.Events[^1]);
    }
}