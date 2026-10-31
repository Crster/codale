using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class FeatureHistoryRebuilderTests
{
    [Fact]
    public async Task Rebuild_reads_the_entire_history_including_long_messages_and_refreshes_the_title()
    {
        var model = new RecordingModel();
        var feature = new FeatureInfo { Id = "f", Name = "Old title", Summary = "obsolete summary" };
        var entries = Enumerable.Range(0, 30)
            .Select(i => new HandoffEntry(HandoffRole.User, $"message-{i}:" + new string('x', 8_000), "chat"))
            .Append(new HandoffEntry(HandoffRole.Assistant, new string('y', 13_000) + "long-message-end", "chat"))
            .Append(new HandoffEntry(HandoffRole.TurnEnd, "success", "chat"))
            .ToList();

        var result = await FeatureHistoryRebuilder.RebuildAsync(model, feature, entries);

        var summaries = model.Inputs.Take(model.Inputs.Count - 1).ToList();
        Assert.True(summaries.Count > 1);
        for (var i = 0; i < 30; i++)
        {
            Assert.Contains(summaries, input => input.Contains($"message-{i}:"));
        }
        Assert.Contains(summaries, input => input.Contains("long-message-end"));
        Assert.DoesNotContain("obsolete summary", summaries[0]);
        Assert.Contains("(none yet)", summaries[0]);
        Assert.Contains("rebuilt summary", summaries[1]);
        Assert.Contains("Finished", summaries[0]);
        Assert.Contains("rebuilt summary", model.Inputs[^1]);
        Assert.Equal("New history title", result.Title);
        Assert.Equal("rebuilt summary", result.Summary);
    }

    [Fact]
    public async Task Empty_summary_fails_instead_of_claiming_completion()
    {
        var model = new RecordingModel { EmptySummary = true };
        await Assert.ThrowsAsync<HelperModelException>(() => FeatureHistoryRebuilder.RebuildAsync(
            model, new FeatureInfo { Id = "f", Name = "History" }, [new(HandoffRole.User, "fix login")]));
        Assert.Single(model.Inputs);
    }

    [Fact]
    public async Task Archived_only_or_empty_logs_can_still_refresh_the_title_from_saved_context()
    {
        var model = new RecordingModel();
        var result = await FeatureHistoryRebuilder.RebuildAsync(
            model, new FeatureInfo { Id = "f", Name = "History", Summary = "saved context" }, []);
        Assert.Single(model.Inputs);
        Assert.Contains("saved context", model.Inputs[0]);
        Assert.Equal("saved context", result.Summary);
    }

    private sealed class RecordingModel : IHelperModel
    {
        public bool IsAvailable => true;
        public bool EmptySummary { get; init; }
        public List<string> Inputs { get; } = [];

        public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default)
        {
            Inputs.Add(prompt);
            return Task.FromResult(systemPrompt == FeatureSummarizer.SystemPrompt
                ? EmptySummary ? "" : "rebuilt summary"
                : "\"New history title\"\nExtra commentary");
        }

        public Task<ToolCall?> CallToolAsync(string systemPrompt, string conversation,
            IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
