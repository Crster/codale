using System.Text.Json;
using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class CodexPlanModeTests
{
    [Theory]
    [InlineData("auto", true)]
    [InlineData("plan", false)]
    public async Task Plan_turns_use_native_planning_and_read_only_access(string mode, bool planOnly)
    {
        await using var session = Create(mode);
        var parameters = new Dictionary<string, object?>();
        session.ApplyTurnOverrides(parameters, false, planOnly);
        var json = JsonSerializer.SerializeToElement(parameters);
        Assert.Equal("on-request", json.GetProperty("approvalPolicy").GetString());
        Assert.Equal("readOnly", json.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        var collaboration = json.GetProperty("collaborationMode");
        Assert.Equal("plan", collaboration.GetProperty("mode").GetString());
        var settings = collaboration.GetProperty("settings");
        Assert.Equal("test-model", settings.GetProperty("model").GetString());
        Assert.Equal("high", settings.GetProperty("reasoning_effort").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("developer_instructions").ValueKind);
    }

    [Fact]
    public async Task Leaving_plan_mode_explicitly_resets_collaboration_and_permissions()
    {
        await using var session = Create("plan");
        await session.TrySetPermissionModeAsync("acceptEdits");
        var parameters = new Dictionary<string, object?>();
        session.ApplyTurnOverrides(parameters, false);
        var json = JsonSerializer.SerializeToElement(parameters);
        Assert.Equal("default", json.GetProperty("collaborationMode").GetProperty("mode").GetString());
        Assert.Equal("dangerFullAccess", json.GetProperty("sandboxPolicy").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ask_turn_does_not_inherit_planning_collaboration()
    {
        await using var session = Create("plan");
        var parameters = new Dictionary<string, object?>();
        session.ApplyTurnOverrides(parameters, true, true);
        var json = JsonSerializer.SerializeToElement(parameters);
        Assert.Equal("default", json.GetProperty("collaborationMode").GetProperty("mode").GetString());
        Assert.Equal("untrusted", json.GetProperty("approvalPolicy").GetString());
    }

    [Fact]
    public async Task Final_plan_waits_for_review_and_decline_finishes_without_implementation()
    {
        await using var session = Create("plan");
        await session.HandleNotificationAsync("item/started", Plan("draft"));
        Assert.False(session.Events.TryRead(out _));
        await session.HandleNotificationAsync("item/completed", Plan("Final plan"));
        Assert.False(session.Events.TryRead(out _));
        await session.HandleNotificationAsync("turn/completed", Completion("completed"));

        Assert.True(session.Events.TryRead(out var started));
        var call = Assert.IsType<ToolCallStarted>(started);
        Assert.Equal("ExitPlanMode", call.ToolName);
        Assert.Equal("Final plan", call.Input.GetProperty("plan").GetString());
        Assert.True(session.Events.TryRead(out var requested));
        var approval = Assert.IsType<ApprovalRequested>(requested);
        Assert.Equal(call.ToolUseId, approval.ToolUseId);
        Assert.False(session.Events.TryRead(out _));

        await session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Deny("Declined"));
        Assert.True(session.Events.TryRead(out var ended));
        Assert.True(Assert.IsType<ToolCallCompleted>(ended).IsError);
        Assert.True(session.Events.TryRead(out var completed));
        Assert.Equal("success", Assert.IsType<TurnCompleted>(completed).Subtype);
        await session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow());
        Assert.False(session.Events.TryRead(out _));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task Unsuccessful_turn_does_not_offer_a_partial_plan(string status)
    {
        await using var session = Create("plan");
        await session.HandleNotificationAsync("item/completed", Plan("Partial"));
        await session.HandleNotificationAsync("turn/completed", Completion(status));
        Assert.True(session.Events.TryRead(out var completed));
        Assert.IsType<TurnCompleted>(completed);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Stop_during_plan_review_completes_locally_and_ignores_late_approval()
    {
        await using var session = Create("plan");
        await session.HandleNotificationAsync("item/completed", Plan("Final plan"));
        await session.HandleNotificationAsync("turn/completed", Completion("completed"));
        Assert.True(session.Events.TryRead(out _));
        Assert.True(session.Events.TryRead(out var request));
        var approval = Assert.IsType<ApprovalRequested>(request);

        // No thread or running process is needed: the remote turn has already ended.
        await session.InterruptAsync();
        Assert.True(session.Events.TryRead(out var ended));
        Assert.True(Assert.IsType<ToolCallCompleted>(ended).IsError);
        Assert.True(session.Events.TryRead(out var completion));
        Assert.Equal("interrupted", Assert.IsType<TurnCompleted>(completion).Subtype);
        await session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow());
        Assert.False(session.Events.TryRead(out _));
    }

    private static CodexAgentSession Create(string mode) => new(new CodexSessionOptions
    {
        WorkingDirectory = ".", Model = "test-model", Effort = "high", PermissionMode = mode,
    });

    private static JsonElement Plan(string text) => JsonSerializer.SerializeToElement(new
    {
        item = new { type = "plan", id = "proposal", text },
    });

    private static JsonElement Completion(string status) => JsonSerializer.SerializeToElement(new
    {
        turn = new { id = "turn", status },
    });
}
