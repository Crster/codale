using Codale.Db;
using Codale.Search.Engine;

namespace Codale.Search;

/// <summary>
/// The file graph, kept in the database's graph (named links between nodes) under <c>graph.WORKTREE.PATH</c>:
/// <list type="bullet">
/// <item><c>pair</c>: files that are one unit split in parts - a view and its code-behind (WorkspacePage.xaml,
/// WorkspacePage.xaml.cs), a class and its partial files (ChatViewModel.cs, ChatViewModel.Features.cs).</item>
/// <item><c>uses</c> / <c>usedby</c>: a file naming a type that one or two other files declare. A type used
/// all over the project relates nothing, so widely used types make no links.</item>
/// </list>
/// A request lands on one file of such a group and usually means the others too: retrieval follows the links.
/// </summary>
public sealed partial class ProjectKnowledgeIndex
{
    public const string PairRelation = "pair";
    public const string UsesRelation = "uses";
    public const string UsedByRelation = "usedby";
    private const int GraphVersion = 1;
    private const int MaxDefiningFiles = 2;
    private const int MaxUsers = 25;
    private const int MaxPairGroup = 12;

    private DbNode GraphRoot(string worktree) => db.Open("graph").Open(worktree);

    /// <summary>Per worktree: the snapshot the stored graph was built from.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _graphs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebuilds the links once per new snapshot; a failure leaves the old links and never stops indexing.</summary>
    private void RefreshGraph(string worktree, SourceSnapshot snapshot)
    {
        if (_graphs.TryGetValue(worktree, out var built) && built == snapshot.BuiltAt) return;
        try
        {
            UpdateGraph(worktree, snapshot);
            _graphs[worktree] = snapshot.BuiltAt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"File graph update failed: {ex.Message}");
        }
    }

    /// <summary>The files linked to <paramref name="path"/> and how; empty before the graph is built.</summary>
    public IReadOnlyList<(string Path, string Relation)> RelatedFiles(string worktree, string path)
    {
        worktree = Core.Projects.ProjectPaths.Normalize(worktree);
        var node = GraphRoot(worktree).Open(path.Replace('/', Path.DirectorySeparatorChar));
        var related = new List<(string, string)>();
        foreach (var relation in (string[])[PairRelation, UsesRelation, UsedByRelation])
            foreach (var target in node.Links(relation))
                related.Add((target.Segments[^1], relation));
        return related;
    }

    /// <summary>Brings the stored links in line with the snapshot: only links that changed are written.</summary>
    internal void UpdateGraph(string worktree, SourceSnapshot snapshot)
    {
        var root = GraphRoot(worktree);
        var wanted = GraphEdges(snapshot);
        var versionNode = db.Open("graphversion").Open(worktree);
        var rebuilt = versionNode.Read() != GraphVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (rebuilt) root.Remove();
        var existing = root.Children().ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.Batch(() =>
        {
            foreach (var path in existing.Where(p => !wanted.ContainsKey(p))) root.Open(path).Remove();
            // Every node exists before any link points at it: a link to an empty node is not followed.
            foreach (var path in wanted.Keys.Where(p => !existing.Contains(p))) root.Open(path).Write("1");
        });
        // A few hundred files per batch, so searches are held up only briefly.
        foreach (var block in wanted.Chunk(200))
        {
            _stopping.Token.ThrowIfCancellationRequested();
            db.Batch(() =>
            {
            foreach (var (path, edges) in block)
            {
                var node = root.Open(path);
                var have = new HashSet<(string, string)>();
                foreach (var relation in (string[])[PairRelation, UsesRelation, UsedByRelation])
                    foreach (var target in node.Links(relation))
                        have.Add((relation, target.Segments[^1]));
                foreach (var (relation, target) in have.Where(h => !edges.Contains(h))) node.Unlink(relation, root.Open(target));
                foreach (var (relation, target) in edges.Where(e => !have.Contains(e)))
                    node.Link(relation, root.Open(target));
            }
            });
        }

        versionNode.Write(GraphVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Every file's links, from the snapshot's names and declarations.</summary>
    internal static Dictionary<string, HashSet<(string Relation, string Target)>> GraphEdges(SourceSnapshot snapshot)
    {
        var files = snapshot.Files.Where(f => Eligible(f.RelativePath) && !f.IsTestOrDoc).ToList();
        var edges = files.ToDictionary(f => f.RelativePath, _ => new HashSet<(string, string)>(), StringComparer.OrdinalIgnoreCase);
        void Add(string from, string relation, string to)
        {
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && edges.TryGetValue(from, out var set) && edges.ContainsKey(to))
                set.Add((relation, to));
        }

        // One unit in parts: the same name up to its first dot, in the same folder.
        foreach (var group in files.GroupBy(f => (Path.GetDirectoryName(f.RelativePath) ?? "") + "|" +
            Path.GetFileName(f.RelativePath).Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count is < 2 or > MaxPairGroup || group.Key.EndsWith('|')) continue;
            foreach (var a in members)
                foreach (var b in members)
                    Add(a.RelativePath, PairRelation, b.RelativePath);
        }

        // Uses: a specific type, declared in one or two files, named by a few others.
        var declared = files.SelectMany(f => f.Symbols.Where(s => s.Kind == SourceSymbolKind.Type && s.Name.Length >= 4).Select(s => s.Name))
            .Distinct(StringComparer.Ordinal);
        foreach (var name in declared)
        {
            var definers = snapshot.Definitions(name).Where(d => d.Symbol.Name == name && d.Symbol.Kind == SourceSymbolKind.Type)
                .Select(d => d.File).Where(f => edges.ContainsKey(f.RelativePath)).Distinct().ToList();
            if (definers.Count is 0 or > MaxDefiningFiles || snapshot.PostingOf(name.ToLowerInvariant()) is not { } posting) continue;
            var users = posting.Files.Select(id => snapshot.Files[id]).Where(f => !definers.Contains(f) && edges.ContainsKey(f.RelativePath)).ToList();
            if (users.Count is 0 or > MaxUsers) continue;
            foreach (var user in users)
                foreach (var definer in definers)
                {
                    Add(user.RelativePath, UsesRelation, definer.RelativePath);
                    Add(definer.RelativePath, UsedByRelation, user.RelativePath);
                }
        }

        return edges;
    }
}
