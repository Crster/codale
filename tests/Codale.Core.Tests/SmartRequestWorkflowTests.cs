using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class SmartRequestWorkflowTests
{
    private sealed class RecordingStatus : ISmartRequestStatus
    {
        public List<(string Kind, string Task, string Outcome, string? Error)> Events { get; } = [];
        public void Start(string id, string task) => Events.Add(("start", task, "", null));
        public void Finish(string id, string outcome, string? error = null) => Events.Add(("finish", "", outcome, error));
    }

    [Fact]
    public async Task ACompletedRequestReportsStartAndCompletion()
    {
        var status = new RecordingStatus();

        var result = await SmartRequestWorkflow.RunTrackedAsync(status, "decide",
            _ => Task.FromResult<string?>("answer"), CancellationToken.None);

        Assert.Equal("answer", result);
        Assert.Equal(new[] { ("start", "decide", "", (string?)null), ("finish", "", "completed", (string?)null) }, status.Events);
    }

    [Fact]
    public async Task ACancelledRequestIsReportedAndRethrown()
    {
        var status = new RecordingStatus();

        await Assert.ThrowsAsync<OperationCanceledException>(() => SmartRequestWorkflow.RunTrackedAsync<string>(status, "decide",
            _ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.Equal("cancelled", status.Events[^1].Outcome);
    }

    [Fact]
    public async Task AFailedRequestIsReportedWithItsMessageAndGivesNull()
    {
        var status = new RecordingStatus();

        var result = await SmartRequestWorkflow.RunTrackedAsync<string>(status, "decide",
            _ => throw new InvalidOperationException("model unavailable"), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(("finish", "", "error", (string?)"model unavailable"), status.Events[^1]);
    }

    [Fact]
    public async Task ADeadlineWithoutACallerCancelGivesNull()
    {
        var result = await SmartRequestWorkflow.RunWithDeadlineAsync<string>(TimeSpan.FromMilliseconds(20),
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "late";
            }, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ACallerCancelPropagatesThroughTheDeadline()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SmartRequestWorkflow.RunWithDeadlineAsync<string>(
            TimeSpan.FromSeconds(30),
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "late";
            }, caller.Token));
    }

    [Fact]
    public async Task AnAnswerInsideTheDeadlineIsReturned()
    {
        var result = await SmartRequestWorkflow.RunWithDeadlineAsync(TimeSpan.FromSeconds(5),
            _ => Task.FromResult<string?>("quick"), CancellationToken.None);

        Assert.Equal("quick", result);
    }
}
