using Codale.Core.Markdown;

namespace Codale.Core.Tests;

public sealed class PlanDiffTests
{
    [Fact]
    public void An_identical_plan_has_no_changes()
    {
        Assert.Empty(PlanDiff.Compute("# A\n\nOne.\n\nTwo.", "# A\n\nOne.\n\nTwo."));
    }

    [Fact]
    public void An_edited_block_remembers_its_old_wording()
    {
        var changes = PlanDiff.Compute("# A\n\nOne.\n\nTwo.", "# A\n\nOne, revised.\n\nTwo.");

        var change = Assert.Single(changes);
        Assert.Equal(1, change.Key);
        Assert.Equal(PlanChangeKind.Edited, change.Value.Kind);
        Assert.Equal("One.", change.Value.Was);
    }

    [Fact]
    public void A_new_block_is_added_and_a_dropped_one_hangs_off_the_block_before_it()
    {
        var added = PlanDiff.Compute("# A\n\nOne.", "# A\n\nOne.\n\nExtra.");
        Assert.Equal(PlanChangeKind.Added, Assert.Single(added).Value.Kind);

        var dropped = PlanDiff.Compute("# A\n\nOne.\n\nTwo.", "# A\n\nOne.");
        var change = Assert.Single(dropped);
        Assert.Equal(1, change.Key);
        Assert.Equal(PlanChangeKind.DroppedAfter, change.Value.Kind);
        Assert.Equal(["Two."], change.Value.Removed);
    }
}
