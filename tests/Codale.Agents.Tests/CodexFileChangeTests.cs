using System.Text.Json;
using Codale.Agents.Codex;
using Codale.Core.Agents;
using Codale.App.ViewModels;

namespace Codale.Agents.Tests;

public sealed class CodexFileChangeTests
{
    [Theory]
    [InlineData("update", "@@ -2,2 +2,3 @@\n keep\n-old\n+new\n+extra\n", 2, 1)]
    [InlineData("add", "@@ -0,0 +1,2 @@\n+first\n+second\n", 2, 0)]
    [InlineData("delete", "@@ -1,2 +0,0 @@\n-first\n-second\n", 0, 2)]
    public async Task Completed_edits_keep_the_patch_for_session_line_counts(string kind, string patch, int added, int removed)
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        var notification = JsonSerializer.SerializeToElement(new
        {
            item = new
            {
                type = "fileChange", id = "edit", status = "completed",
                changes = new[] { new { path = "one.cs", kind = new { type = kind }, diff = patch } },
            },
        });
        await session.HandleNotificationAsync("item/completed", notification);

        Assert.True(session.Events.TryRead(out var result));
        var change = Assert.Single(Assert.IsType<ToolCallCompleted>(result).FileChanges!);
        Assert.Equal(patch, change.UnifiedDiff);
        var diff = Assert.IsType<Codale.Git.FileDiff>(ToolDiffs.FromChange(change, null));
        Assert.Equal(added, diff.Added);
        Assert.Equal(removed, diff.Removed);
        Assert.Equal(kind == "add", diff.IsNew);
        Assert.Equal(kind == "delete", diff.IsDeleted);
    }

    [Fact]
    public async Task Multi_file_edits_keep_each_files_own_counts()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        using var doc = JsonDocument.Parse("""
            {"item":{"type":"fileChange","id":"edit","status":"completed","changes":[
              {"path":"one.cs","kind":{"type":"update"},"diff":"@@ -1 +1 @@\n-old\n+new\n"},
              {"path":"two.cs","kind":{"type":"update"},"diff":"@@ -1 +1,2 @@\n-old\n+first\n+second\n@@ -10 +11 @@\n-before\n+after\n"}
            ]}}
            """);
        await session.HandleNotificationAsync("item/completed", doc.RootElement);

        Assert.True(session.Events.TryRead(out var result));
        var changes = Assert.IsType<ToolCallCompleted>(result).FileChanges!;
        var first = ToolDiffs.FromChange(changes[0], null)!;
        var second = ToolDiffs.FromChange(changes[1], first)!;
        Assert.Equal((1, 1), (first.Added, first.Removed));
        Assert.Equal((3, 2), (second.Added, second.Removed));
        Assert.Equal("two.cs", second.Path);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("unified_diff")]
    public async Task Path_keyed_changes_keep_their_patch(string field)
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        using var doc = JsonDocument.Parse("""
            {"item":{"type":"fileChange","id":"edit","status":"completed","changes":{
              "one.cs":{"type":"update","FIELD":"@@ -1 +1 @@\n-old\n+new\n"}
            }}}
            """.Replace("FIELD", field));
        await session.HandleNotificationAsync("item/completed", doc.RootElement);

        Assert.True(session.Events.TryRead(out var result));
        var change = Assert.Single(Assert.IsType<ToolCallCompleted>(result).FileChanges!);
        var diff = ToolDiffs.FromChange(change, null)!;
        Assert.Equal((1, 1), (diff.Added, diff.Removed));
    }

    [Theory]
    [InlineData("completed", false, 2)]
    [InlineData("failed", true, 0)]
    [InlineData("declined", true, 0)]
    public async Task File_changes_expose_paths_and_only_record_successful_edits(string status, bool error, int count)
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        var json = """{"item":{"type":"fileChange","id":"edit1","status":"STATUS","changes":[{"path":"one.cs","kind":{"type":"update"},"diff":""},{"path":"two.cs","kind":{"type":"add"},"diff":""} ]}}""".Replace("STATUS", status);
        using var doc = JsonDocument.Parse(json);
        await session.HandleNotificationAsync("item/started", doc.RootElement);
        await session.HandleNotificationAsync("item/completed", doc.RootElement);
        Assert.True(session.Events.TryRead(out var startEvent));
        var start = Assert.IsType<ToolCallStarted>(startEvent);
        Assert.Equal("one.cs", start.Input.GetProperty("file_path").GetString());
        Assert.True(session.Events.TryRead(out var endEvent));
        var end = Assert.IsType<ToolCallCompleted>(endEvent);
        Assert.Equal(error, end.IsError);
        Assert.Equal(count, end.FileChanges?.Count ?? 0);
        if (count > 0)
            Assert.Equal(new[] { FileChangeKind.Update, FileChangeKind.Create }, end.FileChanges!.Select(c => c.Kind));
    }
}
