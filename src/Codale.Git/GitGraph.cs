namespace Codale.Git;

/// <summary>
/// Lays the commit history out as a branch graph: each commit gets a lane, and each row
/// carries the edges drawn down to its parents, the way GitKraken or VS Code draw it.
/// </summary>
/// <remarks>
/// The input must be topologically ordered (the log runs with <c>--topo-order</c>), so a
/// parent is always on a later row than its child. Lanes stay as far left as possible:
/// the first parent inherits its child's lane so a branch's main line runs straight, and
/// a merge's other parents open new lanes on the right. A lane lives exactly as long as
/// the commits it was opened for.
/// </remarks>
public static class GitGraph
{
    public static IReadOnlyList<GitCommit> Assign(IReadOnlyList<GitCommit> commits)
    {
        var assigned = new GitCommit[commits.Count];

        // The commit each lane currently leads to: lane i continues, from row to row,
        // until the commit it was started for has been drawn.
        var lanes = new List<string>();

        for (var row = 0; row < commits.Count; row++)
        {
            var commit = commits[row];

            // A commit appears in exactly one lane: either one opened for it by a child
            // or a branch tip, or - for a root the window starts mid-history on - a lane
            // opened here.
            var lane = lanes.IndexOf(commit.Sha);
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(commit.Sha);
            }

            // The lanes as this row found them, so the pass-through edges below can say
            // where each one ends up once this row's lane has closed.
            var before = lanes.ToArray();

            // This row's own history: the lane dies with the commit and is reborn for
            // its parents, first parent in place, the rest to the right.
            lanes.RemoveAt(lane);

            var own = new List<GitGraphEdge>();
            var first = true;

            foreach (var parent in commit.Parents)
            {
                var parentLane = lanes.IndexOf(parent);
                if (parentLane < 0)
                {
                    parentLane = first ? lane : lanes.Count;
                    lanes.Insert(parentLane, parent);
                }

                own.Add(new GitGraphEdge(lane, parentLane));
                first = false;
            }

            // Pass-through edges: every other lane keeps running past this row. When this
            // row's lane closed without being reborn (a root, or a parent that already had
            // a lane further left) the lanes to its right shift left by one, so the line
            // must end on the shifted lane or it would miss the commit it leads to.
            var edges = new List<GitGraphEdge>();
            for (var i = 0; i < before.Length; i++)
            {
                if (i != lane)
                {
                    edges.Add(new GitGraphEdge(i, lanes.IndexOf(before[i])));
                }
            }

            edges.AddRange(own);

            assigned[row] = commit with
            {
                Lane = lane,
                LaneCount = Math.Max(Math.Max(lanes.Count, before.Length), lane + 1),
                Edges = edges,
            };
        }

        return assigned;
    }
}
