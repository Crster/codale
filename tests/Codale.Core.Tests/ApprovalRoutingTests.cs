using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class ApprovalRoutingTests
{
    [Theory]
    [InlineData("ask")]
    [InlineData("plan")]
    [InlineData("automatic")]
    [InlineData("manual")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void AttachedReadsAreAllowedInEveryMode(string mode) =>
        Assert.Equal(ApprovalStep.AllowAttachedRead, ApprovalRouting.Route(mode, "Read", isAttachedRead: true, runsUnasked: false));

    [Theory]
    [InlineData("automatic", ApprovalStep.AllowUnasked)]
    [InlineData("bypassPermissions", ApprovalStep.AllowUnasked)]
    [InlineData("acceptEdits", ApprovalStep.AllowUnasked)]
    [InlineData("manual", ApprovalStep.ConsultPolicy)]
    [InlineData("default", ApprovalStep.ConsultPolicy)]
    public void CodaleOwnToolsRunUnaskedExceptInManualModes(string mode, ApprovalStep expected) =>
        Assert.Equal(expected, ApprovalRouting.Route(mode, "mcp__codale__tasks", false, runsUnasked: true));

    [Theory]
    [InlineData("ask")]
    [InlineData("plan")]
    [InlineData("automatic")]
    [InlineData("manual")]
    [InlineData("default")]
    public void GovernedModesConsultThePolicyForEveryTool(string mode)
    {
        Assert.Equal(ApprovalStep.ConsultPolicy, ApprovalRouting.Route(mode, "Edit", false, false));
        Assert.Equal(ApprovalStep.ConsultPolicy, ApprovalRouting.Route(mode, "Read", false, false));
        Assert.Equal(ApprovalStep.ConsultPolicy, ApprovalRouting.Route(mode, "Bash", false, false));
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    public void AcceptEditsLetsFileEditsThroughUnasked(string tool) =>
        Assert.Equal(ApprovalStep.AllowEdit, ApprovalRouting.Route("acceptEdits", tool, false, false));

    [Theory]
    [InlineData("Bash")]
    [InlineData("Read")]
    [InlineData("mcp__github__issue")]
    public void AcceptEditsDoesNotAllowOtherTools(string tool) =>
        Assert.Equal(ApprovalStep.Continue, ApprovalRouting.Route("acceptEdits", tool, false, false));

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    public void FullModesLeaveUnaskedToolsForTheLaterChecks(string mode)
    {
        Assert.Equal(ApprovalStep.Continue, ApprovalRouting.Route(mode, "Edit", false, false));
        Assert.Equal(ApprovalStep.Continue, ApprovalRouting.Route(mode, "mcp__github__issue", false, false));
    }

    [Fact]
    public void AttachedReadTakesPrecedenceOverPolicy() =>
        Assert.Equal(ApprovalStep.AllowAttachedRead, ApprovalRouting.Route("ask", "Read", isAttachedRead: true, runsUnasked: true));
}
