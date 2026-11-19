using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Codale.Core.Agents;
using Codale.Core.Shell;

namespace Codale.Agents;

public sealed record SpecialistMessage(string Role, string Text);
public sealed record SpecialistQuestion(string Id, string Text, string[] Options, bool MultiSelect = false);

/// <summary>Persisted separately from the provider transcript, including review state across application restarts.</summary>
public sealed record SpecialistSnapshot
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ParentId { get; init; } = "";
    public bool Planner { get; init; }
    public string OriginalPrompt { get; init; } = "";
    public string Briefing { get; init; } = "";
    public string CliId { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public IReadOnlyList<TurnAttachment> Attachments { get; init; } = [];
    public List<SpecialistMessage> Messages { get; init; } = [];
    public List<SpecialistQuestion> Questions { get; init; } = [];
    public string? Plan { get; init; }
    public int PlanVersion { get; init; }
    public bool HandedOff { get; init; }
    /// <summary>The user skipped planning; the original request went to the parent without a plan.</summary>
    public bool Skipped { get; init; }
    public bool InterviewAnswered { get; init; }
    public string Status { get; init; } = "Ready";
}

/// <summary>A host-managed read-only child, independent of provider-native subagents.</summary>
public sealed class SpecialistSession(IAgentSessionFactory factory, AgentSessionRequest request, SpecialistSnapshot snapshot) : IAsyncDisposable
{
    public SpecialistSnapshot Snapshot { get; private set; } = snapshot;
    public event Action? Changed;
    private IAgentSession? _session;
    private CancellationTokenSource? _lifetime;
    private Task? _pump;

    private ApprovalRequested? _question;
    private bool _running;
    private bool _disposed;
    public bool IsRunning => _running;

    public const string PlannerInstruction =
        "You are a temporary planning specialist. Inspect code but NEVER implement application changes or launch other agents. " +
        "Conduct a fixed deep requirements interview for EVERY request. Cover all of: goals and measurable success criteria; " +
        "audience and workflows; scope and exclusions; current behavior; constraints; configuration and preferences; interfaces and data; " +
        "edge cases and failures; compatibility; testing; rollout. Summarize answers already supplied for explicit confirmation, " +
        "and ask focused rounds about unresolved choices. Do not skip the interview even if the request seems complete. " +
        "Before asking, inspect relevant code and recalled memory. Do not invent requirements. " +
        "Only after the user has answered or confirmed the interview, present a decision-complete plan inside <proposed_plan> tags " +
        "and request review. Codale handles approval and sends the plan to the parent. You must never implement it yourself.";

    public const string AnswerInstruction = "You are the parent's Lite answer specialist. Answer questions only; inspect files and search as needed. " +
        "Never change application files, execute changes, or launch other agents. If the user requests an action, return it to the parent.";

    public async Task SendAsync(string text, IReadOnlyList<TurnAttachment>? attachments = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("The specialist is still answering.");
        if (Snapshot.HandedOff) throw new InvalidOperationException("This plan has already been handed to its parent.");
        Snapshot = Snapshot with { Plan = null, Questions = [], Status = "Starting",
            Attachments = Snapshot.Attachments.Concat(attachments ?? []).DistinctBy(a => a.Path).ToArray() };
        Snapshot.Messages.Add(new("user", text));
        _running = true;
        Changed?.Invoke();
        try
        {
            if (_pump is { IsCompleted: false } priorPump && _session is null) await priorPump;
            if (_session is null)
            {
                _lifetime?.Dispose();
                _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // Resume from our bounded briefing after idle process disposal; pending native requests cannot survive process exit.
                _session = factory.Create(request with
                {
                    ClaudeSessionId = Guid.NewGuid(),
                    ResumeSessionId = null,
                    PermissionMode = Snapshot.Planner ? "plan" : "manual",
                    Model = Snapshot.Model, Effort = Snapshot.Effort,
                    SystemPromptAppend = (Snapshot.Planner ? PlannerInstruction : AnswerInstruction) + "\n" + request.SystemPromptAppend,
                    McpServers = request.McpServers.Select(s => s with { AutoApprove = false, RequireApproval = true }).ToArray(),
                    DisallowedTools = string.Join(',', new[] { request.DisallowedTools, "Agent,Task" }.Where(s => !string.IsNullOrEmpty(s))),
                    PromptSuggestions = false,
                });
                var starting = _session;
                var lifetime = _lifetime;
                await starting.StartAsync(lifetime.Token);
                if (!ReferenceEquals(starting, _session)) return;
                _pump = PumpAsync(starting, lifetime.Token);
            }
            var context = Snapshot.Briefing + "\n\nEarlier child conversation:\n" +
                string.Join("\n\n", Snapshot.Messages.SkipLast(1).TakeLast(20).Select(m => $"{m.Role}: {m.Text}"));
            var sending = _session ?? throw new OperationCanceledException("The specialist was stopped.");
            await sending.SendAsync(new(text)
            {
                PlanOnly = Snapshot.Planner, AskOnly = !Snapshot.Planner,
                Attachments = attachments ?? [], ContextPrefix = SessionBriefing.ConversationBlock(context),
            }, _lifetime?.Token ?? ct);
            if (!ReferenceEquals(sending, _session)) return;
            Snapshot = Snapshot with { Status = "Working", InterviewAnswered = Snapshot.InterviewAnswered || Snapshot.Planner && Snapshot.Messages.Count(m => m.Role == "user") > 1 };
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            _running = false;
            Snapshot = Snapshot with { Status = e is OperationCanceledException ? "Stopped" : "Failed: " + e.Message };
            Changed?.Invoke();
            await CloseProcessAsync();
        }
    }

    private async Task PumpAsync(IAgentSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var item in session.Events.ReadAllAsync(ct))
            {
                if (!ReferenceEquals(session, _session)) return;
                switch (item)
                {
                    case AssistantMessageCompleted { ParentToolUseId: null, Text.Length: > 0 } message:
                        Snapshot.Messages.Add(new("assistant", message.Text));
                        if (Snapshot.Planner && ExtractPlan(message.Text) is { } proposed) SetPlan(proposed);
                        break;
                    case ApprovalRequested approval:
                        if (approval.ToolName == "ExitPlanMode")
                        {
                            var plan = String(approval.Input, "plan") ?? Snapshot.Messages.LastOrDefault(m => m.Role == "assistant")?.Text;
                            if (!string.IsNullOrWhiteSpace(plan)) SetPlan(ExtractPlan(plan) ?? plan);
                            // Do not allow a provider's native plan acceptance to start implementation in the child.
                            await session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Deny(
                                "Codale is reviewing this plan. Stop here; implementation belongs to the parent session."), ct);
                        }
                        else if (ReadQuestions(approval.Input) is { Count: > 0 } questions)
                        {
                            _question = approval;
                            Snapshot = Snapshot with { Questions = questions, Status = "Waiting for answers" };
                        }
                        else
                        {
                            var allow = AllowsInspection(approval, Snapshot.Planner, request.WorkingDirectory, request.AdditionalDirectories);
                            await session.RespondToApprovalAsync(approval.RequestId, allow ? ApprovalDecision.Allow()
                                : ApprovalDecision.Deny("This is a read-only specialist. Inspect and answer in chat; do not implement or launch agents."), ct);
                        }
                        break;
                    case ApprovalCancelled cancelled when _question?.RequestId == cancelled.RequestId:
                        _question = null;
                        Snapshot = Snapshot with { Questions = [] };
                        break;
                    case AgentError error:
                        Snapshot = Snapshot with { Status = "Failed: " + error.Message };
                        break;
                    case TurnCompleted turn:
                        _running = false;
                        Snapshot = Snapshot with { Status = Snapshot.Plan is not null ? Snapshot.InterviewAnswered
                            ? "Plan ready for review" : "Interview confirmation required before approval"
                            : turn.IsError ? "Failed: " + string.Join("; ", turn.Errors) : "Waiting for follow-up" };
                        Changed?.Invoke();
                        // Idle children retain their records, not a CLI process.
                        if (_question is null) { _session = null; await session.DisposeAsync(); }
                        return;
                    case SessionEnded ended:
                        _running = false;
                        Snapshot = Snapshot with { Status = "Stopped: " + (ended.Error ?? ended.ExitCode.ToString()) };
                        _session = null;
                        await session.DisposeAsync();
                        Changed?.Invoke();
                        return;
                }
                Changed?.Invoke();
            }
            _running = false;
            Snapshot = Snapshot with { Status = "Stopped" };
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            _running = false;
            Snapshot = Snapshot with { Status = "Failed: " + e.Message };
            Changed?.Invoke();
            if (ReferenceEquals(session, _session)) { _session = null; await session.DisposeAsync(); }
        }
    }

    public async Task AnswerAsync(IReadOnlyDictionary<string, string> answers, CancellationToken ct = default)
    {
        var text = string.Join("\n", Snapshot.Questions.Select(q => $"{q.Text}: {answers.GetValueOrDefault(q.Id)}"));
        if (Snapshot.Questions.Any(q => string.IsNullOrWhiteSpace(answers.GetValueOrDefault(q.Id))))
            throw new ArgumentException("Answer every question before submitting.");
        if (_question is { } pending && _session is { } session)
        {
            var mapped = new Dictionary<string, string>();
            foreach (var q in Snapshot.Questions) { mapped[q.Id] = answers[q.Id]; mapped[q.Text] = answers[q.Id]; }
            var updated = JsonNode.Parse(pending.Input.GetRawText())!.AsObject();
            updated["answers"] = JsonSerializer.SerializeToNode(mapped);
            await session.RespondToApprovalAsync(pending.RequestId, new()
            {
                Behavior = ApprovalBehavior.Allow,
                UpdatedInput = JsonSerializer.SerializeToElement(updated),
            }, ct);
            Snapshot.Messages.Add(new("user", text));
            Snapshot = Snapshot with { Questions = [], Status = "Working", InterviewAnswered = true };
            _question = null;
            Changed?.Invoke();
        }
        else await SendAsync("Answers to your interview questions:\n" + text, ct: ct);
    }

    private void SetPlan(string plan)
    {
        if (Snapshot.Plan == plan) return;
        Snapshot = Snapshot with { Plan = plan, PlanVersion = Snapshot.PlanVersion + 1, Status = "Plan ready for review" };
    }

    public bool ClaimPlan(int version, bool requireInterview = true)
    {
        if (_running || requireInterview && !Snapshot.InterviewAnswered || Snapshot.Plan is null || Snapshot.HandedOff || Snapshot.PlanVersion != version) return false;
        Snapshot = Snapshot with { HandedOff = true, Status = "Approved · sent to parent" };
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ends planning without a plan: the child stops and is closed to further input.</summary>
    public async Task SkipAsync()
    {
        if (Snapshot.HandedOff) return;
        _running = false;
        Snapshot = Snapshot with { HandedOff = true, Skipped = true, Questions = [], Status = "Skipped" };
        await CloseProcessAsync();
        Changed?.Invoke();
    }

    public void ReleasePlanClaim()
    {
        Snapshot = Snapshot with { HandedOff = false, Status = "Plan ready for review" };
        Changed?.Invoke();
    }

    public static string? ExtractPlan(string text)
    {
        var match = Regex.Match(text, @"<proposed_plan>\s*(.*?)\s*</proposed_plan>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Trim().Length > 0 ? match.Groups[1].Value.Trim() : null;
    }

    public static bool AllowsInspection(ApprovalRequested approval, bool planner, string cwd, IReadOnlyList<string> roots)
    {
        var name = approval.ToolName;
        if (name is "Agent" or "Task" or "Skill" or "SlashCommand" or "EnterPlanMode") return false;
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
            return name.Contains("codale-memory", StringComparison.Ordinal) && (name.EndsWith("__memory_search", StringComparison.Ordinal) || name.EndsWith("__memory_list", StringComparison.Ordinal));
        var command = String(approval.Input, "command");
        return ApprovalPolicy.Decide(planner ? "plan" : "ask", name, command, approval.Input, cwd, roots).Verdict == ApprovalVerdict.Allow;
    }

    private static string? String(JsonElement input, string key) => input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static List<SpecialistQuestion> ReadQuestions(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array) return [];
        return questions.EnumerateArray().Select(q => new SpecialistQuestion(String(q, "id") ?? String(q, "question") ?? "",
            String(q, "question") ?? String(q, "title") ?? "", q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().Select(Option).Where(o => o.Length > 0).ToArray() : [],
            q.ValueKind == JsonValueKind.Object && q.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True))
            .Where(q => q.Text.Length > 0).ToList();

        // "label: description", or the bare label when the option carries no description.
        static string Option(JsonElement o)
        {
            if (o.ValueKind == JsonValueKind.String) return o.GetString()!.Trim();
            var label = String(o, "label")?.Trim() ?? "";
            var description = String(o, "description")?.Trim();
            return string.IsNullOrEmpty(description) ? label : label.Length == 0 ? description : label + ": " + description;
        }
    }

    private async Task CloseProcessAsync()
    {
        _lifetime?.Cancel();
        if (_session is { } session) { _session = null; await session.DisposeAsync(); }
        if (_pump is { } pump) await pump;
        _pump = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _question = null;
    }

    public async Task StopAsync()
    {
        _running = false;
        Snapshot = Snapshot.HandedOff ? Snapshot : Snapshot with { Status = "Stopped", Plan = null, PlanVersion = Snapshot.PlanVersion + 1 };
        await CloseProcessAsync();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await StopAsync(); }
}
