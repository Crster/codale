using Codale.Core.Projects;
using Microsoft.UI.Dispatching;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    private readonly DateTimeOffset _diagnosticCreated = DateTimeOffset.UtcNow;
    private DispatcherQueueTimer? _memoryTimer;
    private string? _diagnosticRawPath;

    private void StartMemoryDiagnostics()
    {
        _memoryTimer = _dispatcher.CreateTimer();
        _memoryTimer.Interval = TimeSpan.FromMinutes(1);
        // A diagnostic timer must never be the object keeping a closed chat alive.
        var weak = new WeakReference<ChatViewModel>(this);
        _memoryTimer.Tick += (timer, _) =>
        {
            if (weak.TryGetTarget(out var chat)) chat.TraceMemory("chat.periodic");
            else timer.Stop();
        };
        _memoryTimer.Start();
        TraceMemory("chat.created");
    }

    internal void TraceMemory(string reason, string details = "")
    {
        try
        {
            var age = DateTimeOffset.UtcNow - _diagnosticCreated;
            var steps = 0;
            var turns = 0;
            foreach (var item in Items)
            {
                if (item is TurnActivityItem turn)
                {
                    turns++;
                    steps += turn.Steps.Count;
                }
            }
            var longSession = age >= TimeSpan.FromMinutes(30) || Items.Count >= 500 || _status.ContextTokens >= 100_000;
            var history = FeatureId is { } id ? ProjectPaths.FeatureDirectory(_projectPath, id) : null;
            MemoryDiagnostics.Sample(reason,
                $"chat={_chatId} session={SessionId ?? ResumeSessionId ?? "none"} feature={FeatureId ?? "none"} " +
                $"cli={CliId} ageSec={age.TotalSeconds:0} longSession={longSession} busy={IsBusy} " +
                $"items={Items.Count} turns={turns} steps={steps} toolCalls={_toolCalls.Count} contextTokens={_status.ContextTokens} " +
                $"project={System.Text.Json.JsonSerializer.Serialize(_projectPath)} " +
                $"worktree={System.Text.Json.JsonSerializer.Serialize(WorktreePath ?? _projectPath)} " +
                $"historyDirectory={System.Text.Json.JsonSerializer.Serialize(history)} " +
                $"rawLog={System.Text.Json.JsonSerializer.Serialize(_diagnosticRawPath)} {details}");
        }
        catch (Exception ex)
        {
            CrashLog.Debug("memory", $"chat sample unavailable: {ex.GetType().Name}");
        }
    }
}
