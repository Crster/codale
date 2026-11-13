using System.Text;

using Codale.Db;

namespace Codale.Workspace;

/// <summary>
/// Chat history in the workspace database: for each history (feature) an append-only list of JSON records.
/// A record is keyed by the byte offset it starts at, as if the list were one file of lines, so the offsets
/// that summaries remember stay meaningful, and the list rotates into numbered archives once a summary has
/// read it.
/// </summary>
/// <remarks>Layout: <c>history.FEATURE.log.OFFSET</c> is the current list, <c>history.FEATURE.arc.N.OFFSET</c> an archive.</remarks>
public sealed class HistoryStore(string directory)
{
    public const string Current = "log";
    private const string Archive = "arc";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Deleted = new(StringComparer.OrdinalIgnoreCase);

    private string DeletedKey(string feature) => Path.GetFullPath(directory) + "|" + feature;

    private static string Offset(long value) => value.ToString("D12");

    private static long SizeOf(string line) => Encoding.UTF8.GetByteCount(line) + 1;

    /// <summary>How many times the list has been archived.</summary>
    public int Generation(string feature) => WorkspaceDb.Use(directory, db => Archives(db, feature).Count);

    /// <summary>Bytes in the current list.</summary>
    public long Length(string feature) => WorkspaceDb.Use(directory, db => LengthOf(db, feature));

    /// <summary>Kept beside the records so an append does not have to list them to find where it goes.</summary>
    private static DbNode LengthNode(CodaleDb db, string feature) => db.Open("history").Open(feature).Open("length");

    private static long LengthOf(CodaleDb db, string feature) =>
        long.TryParse(LengthNode(db, feature).Read(), out var length) ? length : 0;

    /// <summary>Adds one record (a line of JSON) to the current list; nothing once the history was deleted.</summary>
    public void Append(string feature, string line)
    {
        lock (Gate)
        {
            if (Deleted.Contains(DeletedKey(feature)))
            {
                return;
            }

            WorkspaceDb.Use(directory, db => db.Batch(() =>
            {
                var at = LengthOf(db, feature);
                db.Open("history").Open(feature).Open(Current).Open(Offset(at)).Write(line);
                LengthNode(db, feature).Write((at + SizeOf(line)).ToString());
            }));
        }
    }

    /// <summary>Every generation of the history, oldest first, the current one last; each name is passed back to <see cref="Lines"/>.</summary>
    public IReadOnlyList<string> Generations(string feature) => WorkspaceDb.Use(directory, db =>
    {
        var names = Archives(db, feature).Select(n => Archive + "." + n).ToList();
        if (db.Open("history").Open(feature).Open(Current).ChildCount() > 0)
        {
            names.Add(Current);
        }

        return (IReadOnlyList<string>)names;
    });

    /// <summary>The records of a generation from a byte offset on, in order.</summary>
    public IReadOnlyList<string> Lines(string feature, string generation, long fromOffset = 0) => WorkspaceDb.Use(directory, db =>
    {
        var node = Node(db, feature, generation);
        var lines = new List<string>();
        foreach (var key in node.Children())
        {
            if (long.Parse(key) >= fromOffset && node.Open(key).Read() is { } line)
            {
                lines.Add(line);
            }
        }

        return (IReadOnlyList<string>)lines;
    });

    /// <summary>
    /// Archives the first <paramref name="offset"/> bytes and starts the new current list with whatever was
    /// appended past them, so no record ever lands in an archive unsummarised.
    /// </summary>
    public void ArchiveUpTo(string feature, long offset)
    {
        lock (Gate)
        {
            WorkspaceDb.Use(directory, db =>
            {
                var history = db.Open("history").Open(feature);
                var current = history.Open(Current);
                var keys = current.Children();
                if (keys.Count == 0)
                {
                    return;
                }

                var next = Archives(db, feature).DefaultIfEmpty(0).Max() + 1;
                var records = keys.Select(k => (Start: long.Parse(k), Line: current.Open(k).Read() ?? "")).ToList();
                db.Batch(() =>
                {
                    current.Remove();
                    var archive = history.Open(Archive).Open(next.ToString());
                    var rebased = 0L;
                    foreach (var (start, line) in records)
                    {
                        if (start < offset)
                        {
                            archive.Open(Offset(start)).Write(line);
                        }
                        else
                        {
                            history.Open(Current).Open(Offset(rebased)).Write(line);
                            rebased += SizeOf(line);
                        }
                    }

                    LengthNode(db, feature).Write(rebased.ToString());
                });
            });
        }
    }

    /// <summary>Removes the whole history; late writers of it do nothing.</summary>
    public void Delete(string feature)
    {
        lock (Gate)
        {
            Deleted.Add(DeletedKey(feature));
            WorkspaceDb.Use(directory, db => db.Open("history").Open(feature).Remove());
        }
    }

    private static List<int> Archives(CodaleDb db, string feature) =>
        db.Open("history").Open(feature).Open(Archive).Children()
            .Select(n => int.TryParse(n, out var number) ? number : 0)
            .Order()
            .ToList();

    private static DbNode Node(CodaleDb db, string feature, string generation) =>
        generation == Current
            ? db.Open("history").Open(feature).Open(Current)
            : db.Open("history").Open(feature).Open(Archive).Open(generation[(Archive.Length + 1)..]);
}
