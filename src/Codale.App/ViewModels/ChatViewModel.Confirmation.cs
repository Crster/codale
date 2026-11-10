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
            var result = await SmartExperienceHost.Instance.AssessCommandAsync(
                new(_projectPath, _projectPath, "command-confirmation", _chatId, DateTimeOffset.UtcNow.AddSeconds(8)),
                command, baseline.FormatAssessment(), ct).WaitAsync(ct).ConfigureAwait(false);
            if (result?.Value is not { } improved || ct.IsCancellationRequested || answered.IsCompleted) return;
            var call = new ToolCall("assess_command", JsonSerializer.SerializeToElement(new
            {
                description = improved.Description, risk = improved.Risk, reason = improved.Reason,
            }));
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
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Optional enhancement must never hide the card or block the user's answer.
        }
    }
}
