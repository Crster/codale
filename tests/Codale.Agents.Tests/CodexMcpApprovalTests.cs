using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class CodexMcpApprovalTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static Task StartAsync(CodexAgentSession session, string id, string command, string thread = "main", string turn = "turn-1") =>
        session.HandleNotificationAsync("item/started", Json(new
        {
            threadId = thread, turnId = turn,
            item = new { type = "mcpToolCall", id, server = "codale-tasks", tool = "start_task", arguments = new { command } },
        }));

    private static async Task<ApprovalRequested> RequestAsync(CodexAgentSession session, string command)
    {
        // Actual Codex MCP approval shape: no itemId, server/tool fields or arguments.
        await session.HandleServerRequestAsync(Json(7), "mcpServer/elicitation/request", Json(new
        {
            @params = new
            {
                threadId = "main", turnId = "turn-1", serverName = "codale-tasks", mode = "form",
                _meta = new { codex_approval_kind = "mcp_tool_call", tool_params = new { command } },
                message = "Allow the codale-tasks MCP server to run tool \"start_task\"?",
                requestedSchema = new { type = "object", properties = new { } },
            },
        }));
        var approvals = new List<ApprovalRequested>();
        while (session.Events.TryRead(out var item))
        {
            if (item is ApprovalRequested approval) approvals.Add(approval);
        }

        return Assert.Single(approvals);
    }

    [Fact]
    public async Task Task_confirmation_uses_the_existing_tool_row_and_command()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");
        await StartAsync(session, "build", "dotnet build");
        await StartAsync(session, "test", "dotnet test");
        await StartAsync(session, "other-turn", "dotnet build", turn: "turn-2");
        await StartAsync(session, "other-thread", "dotnet build", thread: "worker");

        var approval = await RequestAsync(session, "dotnet build");

        Assert.Equal("build", approval.ToolUseId);
        Assert.Equal("mcp__codale-tasks__start_task", approval.ToolName);
        Assert.Equal("dotnet build", approval.Input.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Completed_calls_are_not_reused_for_confirmation()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");
        await StartAsync(session, "old", "dotnet build");
        await session.HandleNotificationAsync("item/completed", Json(new
        {
            threadId = "main", turnId = "turn-1",
            item = new { type = "mcpToolCall", id = "old", status = "completed" },
        }));
        await StartAsync(session, "new", "dotnet build");

        Assert.Equal("new", (await RequestAsync(session, "dotnet build")).ToolUseId);
    }

    [Fact]
    public async Task Ambiguous_calls_do_not_attach_permission_to_an_arbitrary_row()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");
        await StartAsync(session, "first", "dotnet build");
        await StartAsync(session, "second", "dotnet build");

        Assert.Null((await RequestAsync(session, "dotnet build")).ToolUseId);
    }

    [Theory]
    [InlineData(true, "accept")]
    [InlineData(false, "decline")]
    public void Confirmation_response_uses_the_elicitation_protocol(bool allow, string action)
    {
        var result = Json(CodexAgentSession.BuildElicitationResult(allow ? ApprovalDecision.Allow() : ApprovalDecision.Deny()));

        Assert.Equal(action, result.GetProperty("action").GetString());
        Assert.False(result.TryGetProperty("decision", out _));
        if (allow)
        {
            Assert.Equal(JsonValueKind.Object, result.GetProperty("content").ValueKind);
        }
    }
}
