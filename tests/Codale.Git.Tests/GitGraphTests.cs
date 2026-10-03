namespace Codale.Git.Tests;

/// <summary>
/// The lane-assignment rules that make the history read as a branch graph: a straight
/// line down the left lane for a linear history, one lane per branch forked off it, and
/// lanes that close as soon as their branch is merged back.
/// </summary>
public sealed class GitGraphTests
{
    private static GitCommit Commit(string sha, params string[] parents) => new()
    {
        Sha = sha,
        Subject = sha,
        Author = "t",
        Parents = parents,
    };

    [Fact]
    public void A_linear_history_stays_in_one_lane()
    {
        var rows = GitGraph.Assign([Commit("c3", "c2"), Commit("c2", "c1"), Commit("c1")]);

        Assert.All(rows, row => Assert.Equal(0, row.Lane));
        Assert.All(rows, row => Assert.Equal(1, row.LaneCount));

        // Each commit's single edge runs straight down its own lane; the root commit
        // has no parents, so its row draws no lines at all.
        Assert.All(rows.Take(2), row =>
        {
            var edge = Assert.Single(row.Edges);
            Assert.Equal((0, 0), (edge.From, edge.To));
        });
        Assert.Empty(rows[2].Edges);
    }

    [Fact]
    public void A_forked_branch_opens_the_next_lane()
    {
        // main: a -> d; feature forked at a: b -> c -> d (merged).
        var rows = GitGraph.Assign([
            Commit("d", "a", "c"),
            Commit("c", "b"),
            Commit("b", "a"),
            Commit("a"),
        ]);

        Assert.Equal(0, rows[3].Lane); // a on main
        Assert.Equal(1, rows[2].Lane); // b in the branch lane forked off a
        Assert.Equal(1, rows[1].Lane); // c continues the branch lane
        Assert.Equal(0, rows[0].Lane); // d, the merge, back on main

        // The merge row draws the branch lane back into main: (0 -> 0) for the first
        // parent plus (0 -> 1) reaching over to where c sits.
        Assert.Contains(rows[0].Edges, e => e is { From: 0, To: 0 });
        Assert.Contains(rows[0].Edges, e => e is { From: 0, To: 1 });
    }

    [Fact]
    public void A_lane_closes_once_its_branch_is_merged()
    {
        // After the merge, the graph is one lane wide again.
        var rows = GitGraph.Assign([
            Commit("merge", "main1", "feature"),
            Commit("feature", "main1"),
            Commit("main1"),
        ]);

        Assert.Equal(2, rows[1].LaneCount); // feature lane alive while it is open
        Assert.Equal(1, rows[2].LaneCount); // and gone on the last row, after the merge
    }

    [Fact]
    public void History_starting_mid_branch_gets_its_own_lane()
    {
        // A commit whose parents fall outside the window has no lane waiting for it.
        var rows = GitGraph.Assign([Commit("mid", "older")]);

        Assert.Equal(0, rows[0].Lane);

        var edge = Assert.Single(rows[0].Edges);
        Assert.Equal((0, 0), (edge.From, edge.To));
    }

    [Fact]
    public void A_lane_to_the_right_follows_its_commit_when_a_lane_to_its_left_closes()
    {
        // m merges a and p; a's own parent is p, which already has a lane. a's lane
        // closes into p's, so p shifts from lane 1 to lane 0 on the next row.
        var rows = GitGraph.Assign([
            Commit("m", "a", "p"),
            Commit("a", "p"),
            Commit("p"),
        ]);

        Assert.Equal(0, rows[2].Lane);

        // Every line drawn out of row 1 must end on the lane the next row's commit sits in.
        Assert.All(rows[1].Edges, e => Assert.Equal(rows[2].Lane, e.To));
        Assert.Contains(rows[1].Edges, e => e is { From: 1, To: 0 });
        Assert.Equal(2, rows[1].LaneCount);
    }
}
