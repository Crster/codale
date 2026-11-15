namespace Codale.App.ViewModels;

public sealed partial class TurnActivityItem
{
    public override void RaiseTextScale()
    {
        base.RaiseTextScale();
        foreach (var step in Steps)
        {
            step.TextSettings = TextSettings;
            step.RaiseTextScale();
        }
    }
}

public sealed partial class ExploreGroupStep
{
    public override void RaiseTextScale()
    {
        base.RaiseTextScale();
        foreach (var tool in Tools)
        {
            tool.TextSettings = TextSettings;
            tool.RaiseTextScale();
        }
    }
}

public sealed partial class ToolCallItem
{
    partial void OnApprovalChanged(ApprovalRequestItem? value) => ApplyApprovalTextSettings();

    public override void RaiseTextScale()
    {
        base.RaiseTextScale();
        ApplyApprovalTextSettings();
    }

    private void ApplyApprovalTextSettings()
    {
        if (Approval is not { } approval) return;
        foreach (var question in approval.Questions)
        {
            question.TextSettings = TextSettings;
            question.RaiseTextScale();
            foreach (var option in question.Options)
            {
                option.TextSettings = TextSettings;
                option.RaiseTextScale();
            }
        }
    }
}