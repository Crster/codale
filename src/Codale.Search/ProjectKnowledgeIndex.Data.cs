using Codale.Db.Search;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    private const int IdBlock = 1000;
    private readonly Lock _idGate = new();
    private long _nextId = -1;
    private long _idLimit;
    private sealed record Sequence(long Limit);

    private static string Scope(string value) => FormattableString.Invariant($"{value.Length}:{value}:");

    /// <summary>The next id, unique for the life of the database. Reserved a block at a time, so a crash only skips numbers.</summary>
    private long NextId()
    {
        lock (_idGate)
        {
            if (_nextId < 0 || _nextId >= _idLimit)
            {
                var counter = db.Record<Sequence>().Key("profile-id");
                var start = _nextId < 0 ? counter.Get()?.Limit ?? 0 : _idLimit;
                var limit = start + IdBlock;
                counter.Value(new(limit));
                _nextId = start;
                _idLimit = limit;
            }
            return ++_nextId;
        }
    }

    private static IReadOnlyList<KeywordTerm> Terms(IReadOnlyList<(string Word, bool Prefix)> terms) =>
        [.. terms.Select(t => new KeywordTerm(t.Word, t.Prefix))];
}
