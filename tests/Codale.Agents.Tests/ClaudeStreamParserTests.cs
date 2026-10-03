using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Replays transcripts captured from a real <c>claude --output-format stream-json</c>
/// run. These are byte-for-byte recordings, so they pin the actual protocol rather
/// than our assumptions about it — and they run with no CLI, no network and no tokens.
/// </summary>
public sealed class ClaudeStreamParserTests
{
    private static IReadOnlyList<AgentEvent> Replay(string fixtureName)
    {
        var parser = new ClaudeStreamParser();
        return File.ReadLines(Fixtures.Path(fixtureName))
            .SelectMany(parser.Parse)
            .ToList();
    }

    [Fact]
    public void Approval_request_is_surfaced_with_its_tool_input_and_suggestions()
    {
        var approval = Replay("approval-allow.jsonl").OfType<ApprovalRequested>().Single();

        Assert.Equal("Write", approval.ToolName);
        Assert.Equal("Write", approval.DisplayName);
        Assert.False(string.IsNullOrEmpty(approval.RequestId));
        Assert.Equal("toolu_01TRpX19GY2tSrsqEeZafx3L", approval.ToolUseId);

        // The dialog needs the actual arguments to show what is about to happen.
        Assert.EndsWith("hello.txt", approval.Input.GetProperty("file_path").GetString());
        Assert.Equal("hi", approval.Input.GetProperty("content").GetString());

        var suggestion = Assert.Single(approval.Suggestions);
        Assert.Equal("setMode", suggestion.Type);
        Assert.Equal("acceptEdits", suggestion.Mode);
        Assert.Equal("Allow all edits this session", suggestion.ToDisplayLabel());
    }

    [Fact]
    public void Allowed_tool_call_produces_a_start_and_a_file_change()
    {
        var events = Replay("approval-allow.jsonl");

        var started = events.OfType<ToolCallStarted>().Single();
        Assert.Equal("Write", started.ToolName);

        var completed = events.OfType<ToolCallCompleted>().Single();
        Assert.Equal(started.ToolUseId, completed.ToolUseId);
        Assert.False(completed.IsError);

        var change = Assert.IsType<FileChange>(completed.FileChange);
        Assert.Equal(FileChangeKind.Create, change.Kind);
        Assert.EndsWith("hello.txt", change.FilePath);
        Assert.Equal("hi", change.NewContent);
    }

    [Fact]
    public void Turn_result_carries_cost_and_the_authoritative_context_window()
    {
        var turn = Replay("approval-allow.jsonl").OfType<TurnCompleted>().Last();

        Assert.False(turn.IsError);
        Assert.Equal("success", turn.Subtype);
        Assert.NotNull(turn.TotalCostUsd);
        Assert.True(turn.TotalCostUsd > 0);

        // Read from the CLI rather than a hardcoded per-model table.
        Assert.Equal(200_000, turn.ContextWindow);

        var usage = Assert.IsType<UsageSnapshot>(turn.Usage);
        Assert.True(usage.ContextTokens > 0);
        Assert.True(usage.OutputTokens > 0);
    }

    [Fact]
    public void Rate_limit_event_drives_the_status_bar_remaining_indicator()
    {
        var limits = Replay("approval-denied.jsonl").OfType<RateLimitUpdated>().First().Limits;

        Assert.Equal("allowed", limits.Status);
        Assert.Equal("five_hour", limits.LimitType);
        Assert.False(limits.IsUsingOverage);
        Assert.Equal(0.69, limits.FiveHourUtilization!.Value, 3);
        Assert.Equal(0.10, limits.SevenDayUtilization!.Value, 3);
        Assert.Equal(0.69, limits.WorstUtilization!.Value, 3);
        Assert.NotNull(limits.FiveHourResetsAt);
    }

    [Fact]
    public void Without_a_host_approval_route_the_cli_denies_and_says_so()
    {
        var events = Replay("approval-denied.jsonl");

        // This fixture was captured without --permission-prompt-tool stdio.
        Assert.Empty(events.OfType<ApprovalRequested>());

        var denied = events.OfType<PermissionDenied>().Single();
        Assert.Equal("Write", denied.ToolName);
        Assert.Contains("haven't granted it yet", denied.Message);
    }

    [Fact]
    public void Session_init_describes_the_environment_the_ui_has_to_render()
    {
        var init = Replay("approval-denied.jsonl").OfType<SessionInitialized>().First();

        Assert.StartsWith("claude-haiku", init.Model);
        Assert.False(string.IsNullOrEmpty(init.WorkingDirectory));
        Assert.Contains("Write", init.Tools);
        Assert.Contains("Bash", init.Tools);
        Assert.NotEmpty(init.SlashCommands);
        Assert.NotEmpty(init.Agents);
        Assert.Equal("2.1.274", init.Version);
    }

    [Fact]
    public void Streaming_deltas_reconstruct_the_text_of_the_completed_message()
    {
        var events = Replay("approval-denied.jsonl");

        var streamed = string.Concat(events.OfType<AssistantTextDelta>().Select(d => d.Text));
        var completed = string.Concat(
            events.OfType<AssistantMessageCompleted>().Select(m => m.Text));

        Assert.False(string.IsNullOrWhiteSpace(streamed));

        // Deltas are a live preview of exactly what the completed messages contain.
        Assert.Equal(completed, streamed);
    }

    /// <summary>
    /// Thinking arrives as its own content block, but the recorded stream shows the
    /// text redacted: every thinking_delta carries "thinking":"" and only the
    /// accompanying signature_delta has a payload. So the UI can show *that* the model
    /// thought (and for how long) but must not promise to show what it thought.
    /// Empty deltas are dropped rather than emitted as blank bubbles.
    /// </summary>
    [Fact]
    public void Thinking_blocks_are_reported_but_their_text_is_redacted()
    {
        var events = Replay("approval-denied.jsonl");

        Assert.Contains(events.OfType<AssistantMessageCompleted>(), m => m.HasThinking);
        Assert.Empty(events.OfType<AssistantThinkingDelta>());
    }

    /// <summary>
    /// Canary for protocol drift: if a CLI update introduces a message we do not
    /// understand, this fails and names it instead of the UI silently dropping it.
    /// </summary>
    [Theory]
    [InlineData("approval-allow.jsonl")]
    [InlineData("approval-denied.jsonl")]
    public void Every_line_in_the_recorded_protocol_is_understood(string fixture)
    {
        var unknown = Replay(fixture).OfType<UnknownEvent>().Select(u => u.RawType).Distinct();
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_prompt_suggestion_becomes_an_event()
    {
        var parser = new ClaudeStreamParser();

        var suggested = Assert.IsType<PromptSuggested>(Assert.Single(parser.Parse(
            "{\"type\":\"prompt_suggestion\",\"suggestion\":\" commit all of this \",\"uuid\":\"u\",\"session_id\":\"s\"}")));

        Assert.Equal("commit all of this", suggested.Text);
        Assert.Empty(parser.Parse("{\"type\":\"prompt_suggestion\",\"suggestion\":\"\"}"));
    }

    [Fact]
    public void Vcs_state_change_maps_to_its_own_event()
    {
        var parser = new ClaudeStreamParser();

        Assert.IsType<VcsStateChanged>(Assert.Single(parser.Parse("{\"type\":\"system\",\"subtype\":\"vcs_state_changed\"}")));
    }

    [Fact]
    public void Garbage_on_stdout_never_throws()
    {
        var parser = new ClaudeStreamParser();

        Assert.Empty(parser.Parse(""));
        Assert.Empty(parser.Parse("   "));
        Assert.IsType<UnknownEvent>(Assert.Single(parser.Parse("not json at all")));
        Assert.IsType<UnknownEvent>(Assert.Single(parser.Parse("{\"type\":\"brand_new_thing\"}")));
    }

    [Theory]
    [InlineData("background_tasks_changed")]
    [InlineData("task_started")]
    [InlineData("task_updated")]
    [InlineData("task_progress")]
    [InlineData("task_notification")]
    public void Background_task_lifecycle_chatter_is_recognised_but_not_shown(string subtype)
    {
        var parser = new ClaudeStreamParser();

        Assert.All(parser.Parse($"{{\"type\":\"system\",\"subtype\":\"{subtype}\"}}"), e => Assert.IsType<CliMessageIgnored>(e));
    }

    [Fact]
    public void Slash_command_discovery_ping_is_recognised_but_not_shown()
    {
        // commands_changed fires when the CLI's slash-command scan settles, usually
        // right after spawn - it carried nothing and used to surface as an
        // "Unrecognised message" warning at the top of every session.
        var parser = new ClaudeStreamParser();

        Assert.IsType<CliMessageIgnored>(Assert.Single(parser.Parse("""{"type":"system","subtype":"commands_changed"}""")));
    }

    [Fact]
    public void Commands_changed_with_a_list_replaces_the_slash_commands()
    {
        var parser = new ClaudeStreamParser();

        var changed = Assert.IsType<CommandsChanged>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"commands_changed","commands":[{"name":"review"},"init"]}""")));

        Assert.Equal(["review", "init"], changed.Commands);
    }

    [Fact]
    public void A_failing_hook_is_reported_and_a_passing_one_is_not()
    {
        var parser = new ClaudeStreamParser();

        var failed = Assert.IsType<HookFailed>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"hook_response","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","exit_code":2,"outcome":"error","stderr":"blocked"}""")));
        Assert.Equal("blocked", failed.Detail);
        Assert.Equal(2, failed.ExitCode);

        Assert.IsType<CliMessageIgnored>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"hook_response","hook_name":"SessionStart:startup","exit_code":0,"outcome":"success"}""")));
    }

    [Fact]
    public void Status_and_informational_messages_map_to_events()
    {
        var parser = new ClaudeStreamParser();

        Assert.Equal("requesting", Assert.IsType<CliStatusChanged>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"status","status":"requesting"}"""))).Status);
        Assert.Equal("hello", Assert.IsType<CliInformation>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"informational","message":"hello"}"""))).Text);
    }

    [Fact]
    public void Api_retry_becomes_a_warning_notice()
    {
        var parser = new ClaudeStreamParser();

        var info = Assert.IsType<CliInformation>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":4000,"error_status":529}""")));

        Assert.Equal("API error 529: retry 2 of 10 in 4s.", info.Text);
        Assert.True(info.IsWarning);

        var bare = Assert.IsType<CliInformation>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"api_retry"}""")));
        Assert.Equal("API error: retrying.", bare.Text);
    }

    [Theory]
    [InlineData("tool_progress")]
    public void Streaming_progress_messages_are_recognised_but_not_shown(string type)
    {
        var parser = new ClaudeStreamParser();

        Assert.IsType<CliMessageIgnored>(Assert.Single(parser.Parse($"{{\"type\":\"{type}\",\"tool_use_id\":\"t1\"}}")));
    }

    [Theory]
    [InlineData("\"type\":\"system\",\"subtype\":\"conversation_reset\"")]
    [InlineData("\"type\":\"conversation_reset\"")]
    public void Slash_clear_maps_to_a_conversation_reset(string body)
    {
        var parser = new ClaudeStreamParser();

        Assert.IsType<ConversationReset>(parser.Parse("{" + body + "}").Single());
    }
}
