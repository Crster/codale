using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.App.ViewModels;

/// <summary>
/// Rebuilding a chat from its history in the workspace database: prompts and answers come back as rows, and
/// each turn's tool activity comes back as the same block a live turn leaves, on demand.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>A call the transcript never shows as a step: task-list bookkeeping and the CLI's own plumbing.</summary>
    private static bool IsQuietCall(string toolName) => ToolKinds.IsTodo(toolName) || ToolKinds.IsPlumbing(toolName);

    /// <summary>
    /// The rows a history replays as. Every prompt and answer is there; a turn that used tools gets one
    /// <see cref="HiddenActivityItem"/> in place of its block, and the narration between its tool calls stays with
    /// that block.
    /// </summary>
    private static IReadOnlyList<ChatItem> ReplayRows(IReadOnlyList<AgentEvent> events)
    {
        var rows = new List<ChatItem>();
        for (var start = 0; start < events.Count;)
        {
            var end = start + 1;
            while (end < events.Count && events[end] is not UserMessageRecorded)
            {
                end++;
            }

            var turn = events.Skip(start).Take(end - start).ToList();
            if (turn[0] is UserMessageRecorded user)
            {
                rows.Add(new UserMessageItem { Text = user.Text, Timestamp = user.Timestamp ?? DateTimeOffset.Now });
            }

            var tools = turn.OfType<ToolCallStarted>().Where(t => !IsQuietCall(t.ToolName)).ToList();
            var lastTool = tools.Count == 0 ? -1 : turn.IndexOf(tools[^1]);
            if (tools.Count > 0)
            {
                rows.Add(new HiddenActivityItem
                {
                    FirstToolUseId = tools[0].ToolUseId,
                    Calls = tools.Count,
                    Timestamp = tools[0].Timestamp ?? DateTimeOffset.Now,
                });
            }

            for (var i = lastTool + 1; i < turn.Count; i++)
            {
                if (turn[i] is AssistantMessageCompleted { Text.Length: > 0 } reply)
                {
                    var item = new AssistantMessageItem { Timestamp = reply.Timestamp ?? DateTimeOffset.Now };
                    item.Complete(reply.Text);
                    rows.Add(item);
                }
            }

            start = end;
        }

        return rows;
    }

    /// <summary>The turn's events around a tool call: from its prompt to the next one.</summary>
    private static IReadOnlyList<AgentEvent> TurnAround(IReadOnlyList<AgentEvent> events, string toolUseId)
    {
        var at = events.ToList().FindIndex(e => e is ToolCallStarted t && t.ToolUseId == toolUseId);
        if (at < 0)
        {
            return [];
        }

        var first = at;
        while (first > 0 && events[first - 1] is not UserMessageRecorded)
        {
            first--;
        }

        var last = at;
        while (last + 1 < events.Count && events[last + 1] is not UserMessageRecorded)
        {
            last++;
        }

        return events.Skip(first).Take(last - first + 1).ToList();
    }

    /// <summary>The activity block of one turn, built from what its history recorded: its steps, outputs, diffs and time.</summary>
    private TurnActivityItem BuildActivity(IReadOnlyList<AgentEvent> turn)
    {
        var lastTool = turn.ToList().FindLastIndex(e => e is ToolCallStarted t && !IsQuietCall(t.ToolName));
        var first = turn.Select(e => e.Timestamp).FirstOrDefault(t => t is not null);
        var last = turn.Select(e => e.Timestamp).LastOrDefault(t => t is not null);
        var activity = new TurnActivityItem
        {
            HasClock = first is not null && last is not null,
            Elapsed = first is { } from && last is { } to ? to - from : TimeSpan.Zero,
            Timestamp = first ?? DateTimeOffset.Now,
            Loaded = true,
        };

        var calls = new Dictionary<string, ToolCallItem>();
        for (var i = 0; i < turn.Count; i++)
        {
            switch (turn[i])
            {
                case AssistantMessageCompleted { Text.Length: > 0 } narration when i < lastTool:
                    var note = new AssistantMessageItem { Timestamp = narration.Timestamp ?? DateTimeOffset.Now };
                    note.Complete(narration.Text);
                    activity.Steps.Add(note);
                    break;

                case ToolCallStarted started when !IsQuietCall(started.ToolName):
                    var call = CreateToolCall(started.ToolUseId, started.ToolName, started.Input, started.Timestamp);
                    calls[started.ToolUseId] = call;
                    AddStep(activity, call);
                    break;

                case ToolCallCompleted completed when calls.TryGetValue(completed.ToolUseId, out var done):
                    done.Status = completed.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                    done.Duration = completed.Timestamp - done.Timestamp is { } took && took > TimeSpan.Zero ? took : null;
                    done.Output = done.IsPlan ? null : CapOutput(completed.ResultText);
                    foreach (var change in completed.FileChanges ?? (completed.FileChange is { } single ? [single] : []))
                    {
                        if (ToolDiffs.FromChange(change, done.Diff) is { } diff && done.Diff is null)
                        {
                            done.Diff = diff;
                        }
                    }

                    done.AutoCollapse();
                    done.ReleaseInput();
                    break;
            }
        }

        // A call the history never saw finish was cut off with the session.
        foreach (var unfinished in calls.Values.Where(c => c.Status == ToolCallStatus.Running))
        {
            unfinished.Status = ToolCallStatus.Failed;
        }

        activity.Finish(TurnOutcome.Completed);
        return activity;
    }

    /// <summary>Reads a turn's tool activity back from the history and puts its block where the placeholder was.</summary>
    public void LoadActivity(HiddenActivityItem placeholder)
    {
        if (_featureLog is null)
        {
            return;
        }

        var at = Items.IndexOf(placeholder);
        if (at < 0)
        {
            return;
        }

        var turn = TurnAround(_featureLog.ReadTail(int.MaxValue, out _), placeholder.FirstToolUseId);
        if (turn.Count == 0)
        {
            Items.RemoveAt(at);
            Reflow();
            return;
        }

        Items[at] = BuildActivity(turn);
        Reflow();
    }
}
