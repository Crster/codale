using System.Text.Json;

using Codale.Agents.History;
using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class FeatureLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codale-featurelog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private FeatureLog Recorded()
    {
        var log = new FeatureLog(_directory);
        log.Attach("claude");
        log.AppendUser("fix the build");
        log.Append(new ToolCallStarted { ToolUseId = "t1", ToolName = "Edit", Input = Json("{\"file_path\":\"a.cs\"}") });
        log.Append(new ToolCallCompleted { ToolUseId = "t1" });
        log.Append(new AssistantMessageCompleted { MessageId = "m1", Model = "m", Text = "done" });
        return log;
    }

    [Fact]
    public void A_recorded_feature_reads_back_in_order()
    {
        var events = Recorded().ReadTail(100, out var hasMore);

        Assert.False(hasMore);
        Assert.Equal(
            [typeof(UserMessageRecorded), typeof(ToolCallStarted), typeof(ToolCallCompleted), typeof(AssistantMessageCompleted)],
            events.Select(e => e.GetType()).ToArray());
        Assert.Equal("a.cs", ((ToolCallStarted)events[1]).Input.GetProperty("file_path").GetString());
    }

    [Fact]
    public void Subagent_activity_is_not_recorded()
    {
        var log = new FeatureLog(_directory);
        log.AppendUser("go");
        log.Append(new AssistantMessageCompleted { MessageId = "m", Model = "m", Text = "sub", ParentToolUseId = "t0" });
        log.Append(new ToolCallStarted { ToolUseId = "t9", ToolName = "Read", ParentToolUseId = "t0" });
        log.Append(new ToolCallCompleted { ToolUseId = "t9" });

        Assert.Single(log.ReadTail(100, out _));
    }

    [Fact]
    public void The_progress_since_an_offset_is_what_the_summary_has_not_seen()
    {
        var log = Recorded();
        var offset = log.Length;
        log.AppendUser("now the tests");
        log.Append(new AssistantMessageCompleted { MessageId = "m2", Model = "m", Text = "tests added" });

        var (entries, turns) = log.ReadSince(offset);

        Assert.Equal(1, turns);
        Assert.Equal(
            [(HandoffRole.User, "now the tests"), (HandoffRole.Assistant, "tests added")],
            entries.Select(e => (e.Role, e.Text)).ToArray());
    }

    [Fact]
    public void The_latest_task_list_is_found_for_a_reopened_feature()
    {
        var log = new FeatureLog(_directory);
        log.Append(new ToolCallStarted
        {
            ToolUseId = "todo1",
            ToolName = "mcp__codale-tasks__todos_set",
            Input = Json("""{"todos":[{"content":"first","status":"completed"}]}"""),
        });
        log.Append(new ToolCallStarted
        {
            ToolUseId = "todo2",
            ToolName = "mcp__codale-tasks__todos_set",
            Input = Json("""{"todos":[{"content":"second","status":"in_progress"}]}"""),
        });

        var latest = log.LatestTodoUpdate();

        Assert.NotNull(latest);
        Assert.Equal("second", TodoParser.FromToolInput(latest!.Input).Single().Content);
    }

    [Fact]
    public void The_tail_is_limited_and_says_when_there_is_more()
    {
        var log = new FeatureLog(_directory);
        for (var i = 0; i < 10; i++)
        {
            log.AppendUser($"message {i}");
        }

        var tail = log.ReadTail(3, out var hasMore);

        Assert.True(hasMore);
        Assert.Equal(["message 7", "message 8", "message 9"], tail.OfType<UserMessageRecorded>().Select(u => u.Text));
    }

    [Fact]
    public void Archiving_starts_a_fresh_log_and_the_tail_reaches_back_into_the_archive()
    {
        var log = new FeatureLog(_directory);
        log.AppendUser("before");
        log.Archive();
        log.AppendUser("after");

        Assert.True(File.Exists(Path.Combine(_directory, "log.1.jsonl")));
        Assert.Equal(["before", "after"], log.ReadTail(10, out _).OfType<UserMessageRecorded>().Select(u => u.Text));
        Assert.Equal(1, log.ReadSince(0).Turns);
    }

    [Fact]
    public void An_unwritten_feature_has_an_empty_log()
    {
        var log = new FeatureLog(_directory);

        Assert.Equal(0, log.Length);
        Assert.Empty(log.ReadTail(10, out _));
    }
}
