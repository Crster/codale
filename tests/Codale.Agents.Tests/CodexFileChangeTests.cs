using System.Text.Json;
using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class CodexFileChangeTests
{
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
