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

    [Fact]
    public void Deleting_history_prevents_open_and_late_writers_from_recreating_its_log()
    {
        var first = new FeatureLog(_directory, "first");
        var second = new FeatureLog(_directory, "second");
        first.AppendUser("saved request");

        first.Delete();
        second.AppendUser("in-flight request");
        first.EndTurn("success");
        new FeatureLog(_directory, "late").Attach("codex");

        Assert.False(Directory.Exists(_directory));
        Assert.Empty(second.ReadAllSnapshot().Entries);
    }

    [Fact]
    public void Rebuild_snapshot_includes_archives_and_preserves_new_progress_when_compacted()
    {
        var log = new FeatureLog(_directory, "chat");
        log.AppendUser("archived request");
        log.EndTurn("success");
        log.Archive();
        log.AppendUser("current request");
        var snapshot = log.ReadAllSnapshot();

        Assert.Equal(["archived request", "success", "current request"], snapshot.Entries.Select(e => e.Text));
        log.AppendUser("arrived during rebuild");
        log.ArchiveUpTo(snapshot.Length);

        Assert.Equal("arrived during rebuild", Assert.Single(log.ReadSince(0).Entries).Text);
        Assert.Equal(4, log.ReadAllSnapshot().Entries.Count);
    }

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
    public void Each_record_names_the_chat_that_wrote_it_and_a_chat_can_leave_its_own_out()
    {
        var a = new FeatureLog(_directory, "chat-a");
        var b = new FeatureLog(_directory, "chat-b");
        a.AppendUser("add retries");
        b.AppendUser("rename the key");
        b.Append(new AssistantMessageCompleted { MessageId = "m", Model = "m", Text = "renamed" });
        b.EndTurn("success");

        var (all, turns) = new FeatureLog(_directory).ReadSince(0);
        var others = a.ReadSince(0, excludeChat: "chat-a").Entries;

        Assert.Equal(2, turns);
        Assert.Equal(["chat-a", "chat-b", "chat-b", "chat-b"], all.Select(e => e.Chat ?? "").ToArray());
        Assert.Equal(HandoffRole.TurnEnd, all[^1].Role);
        Assert.DoesNotContain(others, e => e.Chat == "chat-a");
        Assert.Equal("rename the key", others[0].Text);
    }

    [Fact]
    public void Archiving_up_to_an_offset_carries_later_lines_into_the_new_log()
    {
        var log = new FeatureLog(_directory, "a");
        log.AppendUser("summarised");
        var offset = log.Length;
        new FeatureLog(_directory, "b").AppendUser("written while the summary was made");

        log.ArchiveUpTo(offset);

        Assert.Equal(["written while the summary was made"], log.ReadSince(0).Entries.Select(e => e.Text));
        Assert.Equal(offset, new FileInfo(Path.Combine(_directory, "log.1.jsonl")).Length);
        Assert.Equal(
            ["summarised", "written while the summary was made"],
            log.ReadTail(10, out _).OfType<UserMessageRecorded>().Select(u => u.Text));
    }

    [Fact]
    public void A_position_survives_archiving()
    {
        var log = new FeatureLog(_directory, "a");
        log.AppendUser("old");
        var seen = log.Position;
        new FeatureLog(_directory, "b").AppendUser("after the position");
        log.Archive();
        new FeatureLog(_directory, "b").AppendUser("after the archive");

        var since = log.ReadSince(seen, excludeChat: "a").Entries.Select(e => e.Text);

        Assert.Equal(["after the position", "after the archive"], since);
    }

    [Fact]
    public void An_unwritten_feature_has_an_empty_log()
    {
        var log = new FeatureLog(_directory);

        Assert.Equal(0, log.Length);
        Assert.Empty(log.ReadTail(10, out _));
    }
}
