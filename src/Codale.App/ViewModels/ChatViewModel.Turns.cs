using Codale.Core.Agents;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    /// <summary>
    /// Closes the active turn: the clock stops, the deliverables - any plan, and the
    /// closing prose - are lifted out below the timeline, and the timeline folds into
    /// its summary line. The whole turn then reads as prompt, what it took, the answer.
    /// </summary>
    private void EndTurn(TurnOutcome outcome)
    {
        _turnTimer.Stop();
        SetActivity(null, toolUseId: null);

        if (_streaming is { } streaming)
        {
            // A turn cut off mid-sentence: the text is final as it stands.
            streaming.PublishPending();
            streaming.IsStreaming = false;
            _streaming = null;
        }

        if (_activeTurn is { } turn)
        {
            turn.Elapsed = DateTimeOffset.Now - _turnStarted;
            turn.HasClock = true;

            foreach (var tool in turn.AllTools)
            {
                // A call whose result never came (interrupt, dead process) would read as
                // running forever - and keep its pulse animating on the UI thread.
                if (tool.IsWorking)
                {
                    tool.Status = outcome == TurnOutcome.Completed ? ToolCallStatus.Succeeded : ToolCallStatus.Failed;
                }

                tool.AutoCollapse();
            }

            SettleTodos(outcome);
            LiftOutDeliverables(turn);
            turn.Finish(outcome);
            _activeTurn = null;

            // The turn the revision request rode on ended without proposing a plan: the
            // agent answered in prose or went off to work. A plan it proposes later,
            // unprompted, is not a revision of that request - the markers must not say so.
            if (ReferenceEquals(_revisionTurn, turn))
            {
                _pendingRevision = null;
                _revisionTurn = null;
            }
        }

        _turnTodo = null;
    }

    /// <summary>
    /// What the turn was for stays in plain view once its timeline folds: a proposed
    /// plan, and the closing prose - the last step, when the turn ended talking rather
    /// than mid-tool. Both move to just below the timeline, plan first.
    /// </summary>
    private void LiftOutDeliverables(TurnActivityItem turn)
    {
        var at = Items.IndexOf(turn);
        if (at < 0)
        {
            return;
        }

        var insert = at + 1;

        foreach (var plan in turn.Steps.OfType<ToolCallItem>().Where(t => t.IsPlan).ToList())
        {
            turn.Steps.Remove(plan);
            Items.Insert(insert++, plan);
        }

        // The task-list row is bookkeeping, not a step: a closing todos_set after the
        // answer must not keep the answer folded inside the timeline.
        if (turn.Steps.LastOrDefault(s => s is not TodoStepItem) is AssistantMessageItem { Text.Length: > 0 } answer &&
            !string.IsNullOrWhiteSpace(answer.Text))
        {
            turn.Steps.Remove(answer);
            Items.Insert(insert, answer);
        }

        Reflow();
    }

    /// <summary>Opens or folds a turn's timeline from its header.</summary>
    public static void ToggleTurn(TurnActivityItem turn)
    {
        if (turn.HasSteps)
        {
            turn.IsExpanded = !turn.IsExpanded;
        }
    }

    /// <summary>
    /// Puts the session into plan mode for the next turn: high effort first - live
    /// when the CLI can switch mid-session, restart-and-resume otherwise - then the
    /// permission-mode switch. When the switch is refused the plan is phrased as an
    /// instruction on the turn itself.
    /// </summary>
    private async Task<bool> EnterPlanModeAsync()
    {
        // Planning twice in a row keeps the first pick: the second would save "high".
        if (!_planEffortRaised)
        {
            _planRestoreEffort = RequestedEffort;
            _planEffortRaised = true;
        }

        RequestedEffort = "high";

        // Mid-turn there is nothing to restart without killing the running turn: the
        // request still goes out in plan mode, and the effort lands on the next turn.
        if (!IsBusy && !string.Equals(_liveEffort, "high", StringComparison.OrdinalIgnoreCase) &&
            !await TryApplyModelEffortLiveAsync(RequestedModel, "high"))
        {
            await RestartWithCurrentConfigAsync();
        }

        var session = _session;
        if (session is null || !IsConnected)
        {
            return false;
        }

        _planSession = session;
        _planRestoreMode = PermissionMode is { Length: > 0 } mode ? mode : DefaultMode;

        if (await session.TrySetPermissionModeAsync("plan"))
        {
            PermissionMode = "plan";
        }

        return true;
    }

    /// <summary>
    /// Automatic mode read a message as a question: the turn runs in ask mode - the CLI
    /// on "manual" so every action comes to the host, which turns it down - and the
    /// plan-turn restore hands Automatic back when it finishes.
    /// </summary>
    private async Task<bool> EnterAskTurnAsync()
    {
        var session = _session;
        if (session is null || !IsConnected)
        {
            return false;
        }

        _planSession = session;
        _planRestoreMode = PermissionMode is { Length: > 0 } mode ? mode : DefaultMode;

        try
        {
            await session.TrySetPermissionModeAsync("manual");
        }
        catch (Exception ex)
        {
            // The turn still carries the explain-only instruction.
            CrashLog.Trace($"Ask turn mode switch failed: {ex.Message}");
        }

        PermissionMode = AskMode;
        return true;
    }

    /// <summary>Planning is done: puts the effort Ctrl+Enter raised back to what it was.</summary>
    private void RestorePlanEffort()
    {
        if (!_planEffortRaised)
        {
            return;
        }

        var effort = _planRestoreEffort;
        _planEffortRaised = false;
        _planRestoreEffort = null;
        RequestedEffort = effort;

        if (_session is null || !IsConnected)
        {
            return;
        }

        // When the live switch is refused the next spawn picks the effort up.
        _ = Task.Run(() => TryApplyModelEffortLiveAsync(RequestedModel, effort));
    }

    /// <summary>Ends a plan turn: hands the session back its previous permission mode.</summary>
    private void RestoreAfterPlanTurn()
    {
        // A subagent still running means the CLI carries on once it reports, in the same
        // plan: leaving plan mode now makes its ExitPlanMode fail ("not in plan mode") and
        // skips the approval. The turn that finishes the plan restores instead.
        if (_planSession is not null && HasActiveSubagents)
        {
            return;
        }

        RestorePlanEffort();

        if (_planSession is null)
        {
            return;
        }

        if (!ReferenceEquals(_session, _planSession))
        {
            _planSession = null;
            _planRestoreMode = null;
            return;
        }

        var session = _planSession;
        var restore = _planRestoreMode ?? DefaultMode;
        _planSession = null;
        _planRestoreMode = null;

        PermissionMode = restore;

        _ = Task.Run(async () =>
        {
            try
            {
                await session.TrySetPermissionModeAsync(CliMode(restore));
            }
            catch (Exception ex)
            {
                CrashLog.Trace($"Plan mode restore failed: {ex.Message}");
            }
        });
    }
}