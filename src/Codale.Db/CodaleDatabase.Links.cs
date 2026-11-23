using System.Text.Json;

using Codale.Db.Engine;

namespace Codale.Db;

public sealed partial class CodaleDatabase
{
    private const string LinksFile = "_links";
    private sealed record StoredLink(string Name, StoredReference Target);

    private static string LinkBucket(StoredReference reference) => Convert.ToBase64String(ReferenceId(reference));

    public LinkEditor Link(string name) => new(this, ValidateKey(name), remove: false);
    public LinkEditor Unlink(string name) => new(this, ValidateKey(name), remove: true);
    public LinkQuery Using(RecordReference record)
    {
        _ = Reference(record);
        return new(this, record);
    }

    internal int EditLinks(string name, IReadOnlyList<RecordReference> records, bool remove)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count < 2) throw new ArgumentException("At least two record references are required.", nameof(records));
        var refs = records.Select(Reference).ToArray();
        return Access(() =>
        {
            if (!remove)
                foreach (var reference in refs.Distinct())
                    if (Address(reference.Type, reference.Key) is null)
                        throw new KeyNotFoundException($"Record '{reference.Key}' does not exist.");
            var changes = new List<IndexChange>();
            var seen = new HashSet<(StoredReference From, StoredReference To)>();
            var edited = 0;
            for (var i = 1; i < refs.Length; i++)
            {
                var from = refs[i - 1];
                var to = refs[i];
                if (!seen.Add((from, to))) continue;
                seen.Add((to, from));
                var bucket = LinkBucket(from);
                var outgoing = JsonSerializer.SerializeToUtf8Bytes(new StoredLink(name, to));
                if ((_storage.Lookup(LinksFile, bucket, outgoing) is not null) != remove) continue;
                changes.Add(new(LinksFile, bucket, outgoing, Value: remove ? null : []));
                if (from != to)
                {
                    var incoming = JsonSerializer.SerializeToUtf8Bytes(new StoredLink(name, from));
                    changes.Add(new(LinksFile, LinkBucket(to), incoming, Value: remove ? null : []));
                }
                edited++;
            }
            _storage.Commit(changes);
            return edited;
        });
    }

    private void StageLinkRemovals(StoredReference reference, List<IndexChange> changes)
    {
        var bucket = LinkBucket(reference);
        var links = _storage.Scan(LinksFile, bucket);
        if (links.Count == 0) return;
        foreach (var row in links)
        {
            var link = JsonSerializer.Deserialize<StoredLink>(row.Id)!;
            if (link.Target == reference) continue;
            var reverse = JsonSerializer.SerializeToUtf8Bytes(new StoredLink(link.Name, reference));
            changes.Add(new(LinksFile, LinkBucket(link.Target), reverse));
        }
        changes.Add(new(LinksFile, bucket, null));
    }

    internal IReadOnlyList<LinkedRecord> Match(RecordReference record, LinkFilter[] filters)
    {
        var reference = Reference(record);
        ArgumentNullException.ThrowIfNull(filters);
        var clauses = filters.ToLookup(filter => (filter.Name, filter.Type));
        var snapshot = Access(() =>
        {
            var rows = _storage.Scan(LinksFile, LinkBucket(reference));
            var candidates = new List<(StoredLink Link, byte[] Address)>();
            foreach (var row in rows)
            {
                var link = JsonSerializer.Deserialize<StoredLink>(row.Id)!;
                if (clauses.Count > 0 && !clauses.Contains((link.Name, link.Target.Type))) continue;
                if (Address(link.Target.Type, link.Target.Key) is { } address) candidates.Add((link, address));
            }
            return candidates;
        });
        var results = new List<LinkedRecord>();
        foreach (var (link, address) in snapshot)
        {
            var bytes = Access(() => _storage.ReadValue(address));
            if (clauses.Count > 0 && !clauses[(link.Name, link.Target.Type)].Any(filter => filter.Predicate(bytes))) continue;
            results.Add(new(this, link.Name, link.Target.Type, link.Target.Key, bytes));
        }
        return results;
    }
}
