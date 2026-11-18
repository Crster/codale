using Codale.Db.Search;

namespace Codale.Search;

/// <summary>
/// What the index keeps in the project's database, read and written straight through the <see cref="Codale.Db.CodaleDb"/>
/// path API. Layout:
/// <list type="bullet">
/// <item><c>compact-source-v1.WORKTREE.PATH</c> = one file's profile (keyword field <c>profile</c>) and, with the
/// Embedding setting on, its vector; see <c>ProjectKnowledgeIndex.Compact.cs</c>.</item>
/// <item><c>turn-topics-v1.WORKTREE.SESSION.TURN</c> = an earlier turn's topic terms and the files it read or edited.</item>
/// <item><c>graph.WORKTREE.PATH</c> = links between files; see <c>ProjectKnowledgeIndex.Graph.cs</c>.</item>
/// <item><c>meta.sequence</c> = the id counter.</item>
/// </list>
/// Features and provider sessions are in <c>ProjectKnowledgeIndex.Features.cs</c>.
/// </summary>
public sealed partial class ProjectKnowledgeIndex
{
    private const int IdBlock = 1000;
    private readonly Lock _idGate = new();
    private long _nextId = -1;
    private long _idLimit;

    /// <summary>The next id, unique for the life of the database. Reserved a block at a time, so a crash only skips numbers.</summary>
    private long NextId()
    {
        lock (_idGate)
        {
            if (_nextId < 0 || _nextId >= _idLimit)
            {
                var counter = db.Open("meta").Open("sequence");
                var start = _nextId < 0 ? (long.TryParse(counter.Read(), out var saved) ? saved : 0) : _idLimit;
                _nextId = start;
                _idLimit = start + IdBlock;
                counter.Write(_idLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return ++_nextId;
        }
    }

    private static IReadOnlyList<KeywordTerm> Terms(IReadOnlyList<(string Word, bool Prefix)> terms) =>
        [.. terms.Select(t => new KeywordTerm(t.Word, t.Prefix))];
}
