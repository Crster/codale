namespace Codale.Core.Markdown;

public enum PlanChangeKind
{
    /// <summary>A block with no counterpart in the old plan.</summary>
    Added,

    /// <summary>A block that replaces old wording.</summary>
    Edited,

    /// <summary>An unchanged block that old blocks were dropped after.</summary>
    DroppedAfter,
}

/// <summary>
/// What a revised plan changed in one of its top-level blocks: the wording it replaced
/// (for an edit) and any old blocks dropped right after it.
/// </summary>
public sealed record PlanBlockChange(PlanChangeKind Kind, string? Was, IReadOnlyList<string> Removed);

/// <summary>
/// Block-level diff of two plan texts, so a revised plan can mark what moved without the
/// reader rereading it all. Blocks are compared by their visible text; a run of old
/// blocks facing a run of new ones is paired in order, so an edited sentence shows its
/// old wording rather than reading as a deletion plus an addition.
/// </summary>
public static class PlanDiff
{
    /// <summary>Changes keyed by the index of the changed block among the new plan's top-level blocks.</summary>
    public static IReadOnlyDictionary<int, PlanBlockChange> Compute(string? oldMarkdown, string? newMarkdown)
    {
        var before = MarkdownParser.Parse(oldMarkdown ?? "").Select(TextOf).ToArray();
        var after = MarkdownParser.Parse(newMarkdown ?? "").Select(TextOf).ToArray();
        var result = new Dictionary<int, PlanBlockChange>();

        // Longest common subsequence: what stayed, in order.
        var lcs = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = before[i] == after[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var removedRun = new List<string>();
        var addedRun = new List<int>();
        var lastKept = 0;

        void Flush()
        {
            for (var k = 0; k < addedRun.Count; k++)
            {
                var was = k < removedRun.Count ? removedRun[k] : null;
                result[addedRun[k]] = new PlanBlockChange(was is null ? PlanChangeKind.Added : PlanChangeKind.Edited, was, []);
            }

            if (removedRun.Count > addedRun.Count)
            {
                var spare = removedRun.Skip(addedRun.Count).ToList();
                var anchor = addedRun.Count > 0 ? addedRun[^1] : lastKept;
                result[anchor] = result.TryGetValue(anchor, out var own)
                    ? own with { Removed = spare }
                    : new PlanBlockChange(PlanChangeKind.DroppedAfter, null, spare);
            }

            removedRun.Clear();
            addedRun.Clear();
        }

        int a = 0, b = 0;
        while (a < before.Length || b < after.Length)
        {
            if (a < before.Length && b < after.Length && before[a] == after[b])
            {
                Flush();
                lastKept = b;
                a++;
                b++;
            }
            else if (b < after.Length && (a == before.Length || lcs[a, b + 1] >= lcs[a + 1, b]))
            {
                addedRun.Add(b++);
            }
            else
            {
                removedRun.Add(before[a++]);
            }
        }

        Flush();
        return result;
    }

    private static string TextOf(MarkdownBlock block) => block switch
    {
        MarkdownBlock.Heading h => TextOf(h.Content),
        MarkdownBlock.Paragraph p => TextOf(p.Content),
        MarkdownBlock.ListItem l => TextOf(l.Content),
        MarkdownBlock.CodeBlock c => c.Text.Trim(),
        MarkdownBlock.Quote q => string.Join("\n", q.Content.Select(TextOf)),
        MarkdownBlock.Table t => string.Join(
            "\n",
            new[] { t.Header }.Concat(t.Rows).Select(row => string.Join(" | ", row.Select(TextOf)))),
        _ => "",
    };

    private static string TextOf(IReadOnlyList<MarkdownInline> content) => string.Concat(content.Select(TextOf)).Trim();

    private static string TextOf(MarkdownInline inline) => inline switch
    {
        MarkdownInline.Text t => t.Value,
        MarkdownInline.CodeSpan c => c.Value,
        MarkdownInline.Emphasis e => TextOf(e.Content),
        MarkdownInline.Strike s => TextOf(s.Content),
        MarkdownInline.Link l => TextOf(l.Content),
        MarkdownInline.Image i => TextOf(i.Content),
        _ => "",
    };
}
