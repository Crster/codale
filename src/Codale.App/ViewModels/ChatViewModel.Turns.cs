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
        RestoreBrowserViewport();
        _turnTimer.Stop();
        _preparingTask = null;
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
    /// Puts the session into plan mode for the next turn: Lite becomes Flash; other
    /// tiers retain their high-effort planning override. The effort switches live
    /// when the CLI can switch mid-session, restart-and-resume otherwise - then the
    /// permission-mode switch. A refused switch leaves the request in the composer.
    /// </summary>
    private async Task<bool> EnterPlanModeAsync()
    {
        // Planning twice in a row keeps the original tier and restoration effort.
        if (!_planEffortRaised)
        {
            _planPromotedFromLite = CurrentTier == "lite";
            _planRestoreEffort = RequestedEffort;
            _planEffortRaised = true;
        }

        // Lite and Flash share the fast model; their difference is low vs medium effort.
        var planEffort = _planPromotedFromLite ? TierConfig("flash").Effort : "high";
        RequestedEffort = planEffort;

        // Mid-turn there is nothing to restart without killing the running turn: the
        // request still goes out in plan mode, and the effort lands on the next turn.
        if (!IsBusy && !string.Equals(_liveEffort, planEffort, StringComparison.OrdinalIgnoreCase) &&
            !await TryApplyModelEffortLiveAsync(RequestedModel, planEffort))
        {
            await RestartWithCurrentConfigAsync();
        }

        var session = _session;
        if (session is null || !IsConnected)
        {
            RestorePlanEffort();
            return false;
        }

        _planSession = session;
        _planRestoreMode = PermissionMode is { Length: > 0 } mode ? mode : DefaultMode;

        bool entered;
        try
        {
            entered = await session.TrySetPermissionModeAsync("plan");
        }
        catch (Exception)
        {
            entered = false;
        }
        if (!entered)
        {
            _planSession = null;
            _planRestoreMode = null;
            RestorePlanEffort();
            ComposerNotice = new NoticeItem
            {
                Text = "Could not enter planning mode. Your request is still in the composer; reconnect and try again.",
                Severity = NoticeSeverity.Warning,
            };
            return false;
        }

        PermissionMode = "plan";
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
        catch (Exception)
        {
            // The turn still carries the explain-only instruction.
        }

        PermissionMode = AskMode;
        _askTurnFromAuto = _planRestoreMode == AutomaticMode;
        return true;
    }

    /// <summary>
    /// Ask mode is still showing from a question Auto routed earlier - its turn-end restore
    /// was skipped (subagents still running, a replaced session, a failed send): hands
    /// Automatic back, host and CLI, so the next message is routed afresh.
    /// </summary>
    private async Task ReturnFromRoutedAskAsync()
    {
        if (!_askTurnFromAuto || PermissionMode != AskMode)
        {
            return;
        }

        _planSession = null;
        _planRestoreMode = null;
        PermissionMode = AutomaticMode;

        if (_session is { } session && IsConnected)
        {
            try
            {
                await session.TrySetPermissionModeAsync(CliMode(AutomaticMode));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Planning is done: restores the original tier's effort without changing saved defaults.</summary>
    private void RestorePlanEffort()
    {
        if (!_planEffortRaised)
        {
            return;
        }

        var effort = _planRestoreEffort;
        _planPromotedFromLite = false;
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
            catch (Exception)
            {
            }
        });
    }
}
