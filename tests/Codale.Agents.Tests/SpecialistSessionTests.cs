using System.Text.Json;
using System.Threading.Channels;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class SpecialistSessionTests
{
    [Fact]
    public void Replay_keeps_recent_history_with_a_fixed_size_budget()
    {
        var snapshot = new SpecialistSnapshot { Briefing = "parent context" };
        snapshot.Messages.Add(new("user", new string('x', 20000)));
        snapshot.Messages.Add(new("assistant", "recent response"));
        snapshot.Messages.Add(new("user", "current request"));
        var replay = SpecialistSession.ReplayContext(snapshot);
        Assert.StartsWith("parent context", replay);
        Assert.Contains("recent response", replay);
        Assert.DoesNotContain("current request", replay);
        Assert.True(replay.Length <= "parent context\n\nEarlier child conversation:\n".Length + 12000);
    }

    private static AgentSessionRequest Request => new()
    {
        CliId = "claude", WorkingDirectory = Path.GetTempPath(), Model = "parent-model", Effort = "medium",
        ClaudeSessionId = Guid.NewGuid(), Environment = new Dictionary<string, string> { ["TEST_PROFILE"] = "profile" },
        AdditionalDirectories = [Path.GetTempPath()], McpServers = [new() { Name = "codale-memory", Command = "memory", AutoApprove = true }],
    };

    [Fact]
    public async Task Answer_child_uses_lite_configuration_unique_identity_and_keeps_parent_request_unchanged()
    {
        var factory = new FakeFactory();
        var parent = Request;
        await using var child = new SpecialistSession(factory, parent, new() { Model = "fast", Effort = "low" });
        await child.SendAsync("why?", [new() { Kind = TurnAttachmentKind.Image, Path = "image.png", Name = "image.png" }]);
        var spawned = factory.Requests.Single();
        Assert.Equal("fast", spawned.Model);
        Assert.Equal("low", spawned.Effort);
        Assert.NotEqual(parent.ClaudeSessionId, spawned.ClaudeSessionId);
        Assert.Same(parent.Environment, spawned.Environment);
        Assert.Equal(parent.AdditionalDirectories, spawned.AdditionalDirectories);
        Assert.False(spawned.McpServers.Single().AutoApprove);
        Assert.True(spawned.McpServers.Single().RequireApproval);
        Assert.Equal("parent-model", parent.Model);
        Assert.True(factory.Last.Turns.Single().AskOnly);
        Assert.Single(factory.Last.Turns.Single().Attachments);
        await factory.Last.Emit(new TurnCompleted { Subtype = "success" });
        await Eventually(() => !child.IsRunning && factory.Last.Disposed);
        await child.SendAsync("how does that work?");
        Assert.Equal(2, factory.Requests.Count);
        Assert.Contains("why?", factory.Last.Turns.Single().ContextPrefix);
        Assert.Single(child.Snapshot.Attachments);
    }

    [Fact]
    public async Task Planner_interview_questions_are_answered_without_approving_implementation()
    {
        var factory = new FakeFactory();
        await using var child = new SpecialistSession(factory, Request, new() { Planner = true, Model = "pro", Effort = "high" });
        await child.SendAsync("build sharing");
        await factory.Last.Emit(new ApprovalRequested { RequestId = "ask", ToolName = "AskUserQuestion",
            Input = JsonSerializer.SerializeToElement(new { questions = new[] { new { id = "audience", question = "Who uses it?", options = new[] { new { label = "Team", description = "Internal" } } } } }) });
        await Eventually(() => child.Snapshot.Questions.Count == 1);
        await child.AnswerAsync(new Dictionary<string, string> { ["audience"] = "Team" });
        Assert.True(child.Snapshot.InterviewAnswered);
        Assert.Equal("Team", factory.Last.Approvals.Single().Decision.UpdatedInput!.Value.GetProperty("answers").GetProperty("audience").GetString());
        Assert.True(factory.Last.Approvals.Single().Decision.UpdatedInput!.Value.TryGetProperty("questions", out _));
        await factory.Last.Emit(new ApprovalRequested { RequestId = "plan", ToolName = "ExitPlanMode", Input = JsonSerializer.SerializeToElement(new { plan = "Approved design" }) });
        await Eventually(() => child.Snapshot.Plan is not null);
        Assert.Equal(ApprovalBehavior.Deny, factory.Last.Approvals.Last().Decision.Behavior);
        await factory.Last.Emit(new TurnCompleted { Subtype = "success" });
        await Eventually(() => !child.IsRunning);
        var version = child.Snapshot.PlanVersion;
        Assert.False(child.ClaimPlan(version + 1));
        Assert.True(child.ClaimPlan(version));
        Assert.False(child.ClaimPlan(version));
    }

    [Fact]
    public async Task Revisions_and_cancellation_invalidate_the_displayed_plan_and_recovery_preserves_review()
    {
        var factory = new FakeFactory();
        var state = new SpecialistSnapshot { Planner = true, Plan = "old plan", PlanVersion = 2, InterviewAnswered = true, ParentId = "parent" };
        var restored = JsonSerializer.Deserialize<SpecialistSnapshot>(JsonSerializer.Serialize(state))!;
        await using var child = new SpecialistSession(factory, Request, restored);
        Assert.Equal("parent", child.Snapshot.ParentId);
        await child.SendAsync("Revise the scope");
        Assert.False(child.ClaimPlan(2));
        await factory.Last.Emit(new AssistantMessageCompleted { MessageId = "reply", Model = "actual-model", Effort = "high", Text = "<proposed_plan>new plan</proposed_plan>" });
        await factory.Last.Emit(new TurnCompleted { Subtype = "success" });
        await Eventually(() => !child.IsRunning);
        Assert.Equal("new plan", child.Snapshot.Plan);
        Assert.True(child.Snapshot.PlanVersion > 2);
        var reply = Assert.Single(child.Snapshot.Messages, message => message.Role == "assistant");
        Assert.Equal("actual-model", reply.Model);
        Assert.Equal("high", reply.Effort);
        await child.StopAsync();
        Assert.Null(child.Snapshot.Plan);
        Assert.False(child.ClaimPlan(child.Snapshot.PlanVersion));
    }

    [Fact]
    public async Task Plan_on_the_first_turn_cannot_skip_the_interview()
    {
        var factory = new FakeFactory();
        await using var child = new SpecialistSession(factory, Request, new() { Planner = true });
        await child.SendAsync("build sharing");
        await factory.Last.Emit(new AssistantMessageCompleted { MessageId = "reply", Model = "pro", Text = "<proposed_plan>premature</proposed_plan>" });
        await factory.Last.Emit(new TurnCompleted { Subtype = "success" });
        await Eventually(() => !child.IsRunning);
        Assert.False(child.ClaimPlan(child.Snapshot.PlanVersion));
        Assert.Contains("Interview", child.Snapshot.Status);
        // Auto Mode's Continue accepts the plan without the interview.
        Assert.True(child.ClaimPlan(child.Snapshot.PlanVersion, requireInterview: false));
    }

    [Fact]
    public async Task Skip_closes_the_planner_and_blocks_further_input()
    {
        var factory = new FakeFactory();
        await using var child = new SpecialistSession(factory, Request, new() { Planner = true });
        await child.SendAsync("build sharing");
        await child.SkipAsync();
        Assert.True(child.Snapshot.Skipped);
        Assert.True(child.Snapshot.HandedOff);
        Assert.False(child.IsRunning);
        Assert.True(factory.Last.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.SendAsync("more"));
    }

    [Fact]
    public void Questions_keep_bare_labels_and_read_multi_select()
    {
        var input = JsonDocument.Parse("""
            {"questions":[{"question":"Which?","multiSelect":true,"options":[{"label":"A"},{"label":"B","description":"second"},"C"]}]}
            """).RootElement;
        var question = SpecialistSession.ReadQuestions(input).Single();
        Assert.True(question.MultiSelect);
        Assert.Equal(["A", "B: second", "C"], question.Options);
    }

    [Theory]
    [InlineData("Read", "{}", true)]
    [InlineData("PowerShell", "{\"command\":\"Get-Content README.md\"}", true)]
    [InlineData("PowerShell", "{\"command\":\"Remove-Item README.md\"}", false)]
    [InlineData("Write", "{\"file_path\":\"src/code.cs\"}", false)]
    [InlineData("Agent", "{}", false)]
    [InlineData("Task", "{}", false)]
    [InlineData("mcp__codale-memory__memory_search", "{}", true)]
    [InlineData("mcp__codale-memory__memory_save", "{}", false)]
    [InlineData("mcp__codale-terminal__run_in_terminal", "{}", false)]
    public void Children_allow_inspection_and_refuse_writes_and_recursive_agents(string name, string input, bool expected)
    {
        var approval = new ApprovalRequested { RequestId = "a", ToolName = name, Input = JsonSerializer.Deserialize<JsonElement>(input) };
        Assert.Equal(expected, SpecialistSession.AllowsInspection(approval, false, Path.GetTempPath(), []));
        Assert.Equal(expected, SpecialistSession.AllowsInspection(approval, true, Path.GetTempPath(), []));
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeFactory : IAgentSessionFactory
    {
        public List<AgentSessionRequest> Requests { get; } = [];
        public FakeSession Last { get; private set; } = null!;
        public IAgentSession Create(AgentSessionRequest request) { Requests.Add(request); return Last = new(); }
    }

    private sealed class FakeSession : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        public string? SessionId => "native";
        public ChannelReader<AgentEvent> Events => _events.Reader;
        public List<UserTurn> Turns { get; } = [];
        public List<(string Id, ApprovalDecision Decision)> Approvals { get; } = [];
        public bool Disposed { get; private set; }
        public ValueTask Emit(AgentEvent item) => _events.Writer.WriteAsync(item);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(UserTurn turn, CancellationToken ct = default) { Turns.Add(turn); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TrySetPermissionModeAsync(string mode, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TrySetModelEffortAsync(string? model, string? effort, CancellationToken ct = default) => Task.FromResult(true);
        public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct = default) { Approvals.Add((requestId, decision)); return Task.CompletedTask; }
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);
        public ValueTask DisposeAsync() { Disposed = true; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
