using System.Text.Json;
using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Shell;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    private async Task EnhanceConfirmationAsync(ToolCallItem pending, ApprovalRequestItem item,
        Task<ApprovalDecision> answered, string command, ShellAssessment baseline, CancellationToken ct)
    {
        try
        {
            var findings = ShellCommandAnalyzer.Analyze(command, _projectPath, ExtraRoots);
            // The Decision model rates the risk in about a second; General (when set) still writes the description.
            using var decisionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            decisionDeadline.CancelAfter(TimeSpan.FromSeconds(8));
            var decided = SmartDecider.IsAvailable
                ? SmartDecider.AskAsync(new { command }, new Dictionary<string, SmartQuestion> { ["risk"] = SmartDecisions.RiskQuestion() }, decisionDeadline.Token)
                : Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(null);
            var described = AppSettings.SmartRoleEnabled(SmartFeatureSettings.General)
                ? SmartExperienceHost.Instance.AssessCommandAsync(
                    new(_projectPath, _projectPath, "command-confirmation", _chatId, DateTimeOffset.UtcNow.AddSeconds(8)),
                    command, baseline.FormatAssessment(), ct)
                : Task.FromResult<Codale.Smart.SmartResult<Codale.Smart.SmartCommandAssessment>?>(null);
            IReadOnlyDictionary<string, SmartDecision>? decision = null;
            try { decision = await decided.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var risk = SmartDecisions.ToRisk(decision?.GetValueOrDefault("risk"));
            if (risk is not null) Apply(baseline.Description, risk, $"Decision model rates this {risk}.");
            var result = await described.WaitAsync(ct).ConfigureAwait(false);
            if (result?.Value is { } improved) Apply(improved.Description, risk ?? improved.Risk, improved.Reason);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Optional enhancement must never hide the card or block the user's answer.
        }

        void Apply(string description, string risk, string reason)
        {
            if (ct.IsCancellationRequested || answered.IsCompleted) return;
            var findings = ShellCommandAnalyzer.Analyze(command, _projectPath, ExtraRoots);
            var call = new ToolCall("assess_command", JsonSerializer.SerializeToElement(new { description, risk, reason }));
            // Reuse the deterministic floor and retain every matched rule's explanation.
            var assessment = ShellCommandAnalyzer.Interpret(call, baseline, findings);
            _dispatcher.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || answered.IsCompleted || !ReferenceEquals(pending.Approval, item)) return;
                item.Description = assessment.Description;
                item.Risk = assessment.Risk;
                item.RiskNote = assessment.FormatRiskNote();
            });
        }
    }
}
