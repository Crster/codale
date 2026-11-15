using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class PlanEffortOverrideTests
{
    [Fact]
    public void PlanningFromAnotherTierRunsAtHighAndRestoresTheStartingEffort()
    {
        var plan = new PlanEffortOverride();

        Assert.Equal("high", plan.Enter("pro", "medium", flashEffort: "medium"));
        Assert.True(plan.IsRaised);
        Assert.Equal("medium", plan.Restore());
        Assert.False(plan.IsRaised);
    }

    [Fact]
    public void LitePlansAtFlashEffortAndShowsItsOwnEffortMeanwhile()
    {
        var plan = new PlanEffortOverride();

        Assert.Equal("medium", plan.Enter("lite", "low", flashEffort: "medium"));
        Assert.True(plan.PromotedFromLite);
        Assert.Equal("medium", plan.ShownEffort("medium"));
    }

    [Fact]
    public void PlanningTwiceKeepsTheOriginalStartingEffort()
    {
        var plan = new PlanEffortOverride();
        plan.Enter("pro", "medium", flashEffort: "medium");

        // The second start sees the raised override, not the planning effort, as the requested one.
        plan.Enter("plus", "high", flashEffort: "medium");

        Assert.Equal("medium", plan.Restore());
    }

    [Fact]
    public void ShownEffortIsTheRestoredEffortWhileRaisedForNonLiteTiers()
    {
        var plan = new PlanEffortOverride();
        plan.Enter("pro", "medium", flashEffort: "medium");

        Assert.Equal("medium", plan.ShownEffort("high"));
    }

    [Fact]
    public void ShownEffortIsTheRequestedEffortWhenNotPlanning()
    {
        var plan = new PlanEffortOverride();

        Assert.Equal("high", plan.ShownEffort("high"));
    }

    [Fact]
    public void RestoreClearsTheOverride()
    {
        var plan = new PlanEffortOverride();
        plan.Enter("lite", "low", flashEffort: "medium");

        plan.Restore();

        Assert.False(plan.PromotedFromLite);
        Assert.Equal("high", plan.ShownEffort("high"));
    }
}
