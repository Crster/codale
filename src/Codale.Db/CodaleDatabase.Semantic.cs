using System.Runtime.InteropServices;
using System.Text.Json;

using Codale.Core.Features;
using Codale.Db.Engine;
using Codale.Db.Search;

namespace Codale.Db;

public sealed partial class CodaleDatabase
{
    private const string SemanticBucket = "semantic";

    private sealed record StoredReference(string Type, string Key);

    private StoredReference Reference(RecordReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ReferenceEquals(reference.Database, this))
            throw new ArgumentException("The reference belongs to another database.", nameof(reference));
        return new(reference.Type, reference.Key);
    }

    private static byte[] ReferenceId(StoredReference reference) => JsonSerializer.SerializeToUtf8Bytes(reference);
    private static string SemanticMembership(StoredReference reference) => "semantic:" + Convert.ToBase64String(ReferenceId(reference));

    public SemanticIndex Index(string name, IEmbedder? embedder = null) => new(this, ValidateKey(name), embedder);
    public string EmbeddingModel { get { lock (_embeddingGate) return _embedder.Model; } }

    internal sealed record PreparedSearch(CodaleDatabase Owner, string File, byte[] Model, byte[] Vector, string? Text);

    internal PreparedSearch PrepareSemantic(string name, string tags, float[]? suppliedVector, SemanticMode mode, IEmbedder? embedder)
    {
        var file = FolderStorage.IndexFile(name);
        var lexical = mode is SemanticMode.Bm25 or SemanticMode.Hybrid;
        var vector = mode is SemanticMode.Vector or SemanticMode.Hybrid;
        var (model, embedding) = vector ? Embed(tags, suppliedVector, embedder) : (string.Empty, Array.Empty<float>());
        if (suppliedVector is not null && (embedding.Length == 0 || !vector))
            throw new ArgumentException("Supply a nonzero vector with vector or hybrid search.", nameof(suppliedVector));
        return new(this, file, embedding.Length == 0 ? [] : Id(model), VectorMath.ToBytes(embedding), lexical ? SearchText.ForIndex(tags) : null);
    }

    internal void IndexSemantic(PreparedSearch search, RecordReference record)
    {
        var reference = Reference(record);
        Access(() =>
        {
            if (Address(reference.Type, reference.Key) is null) throw new KeyNotFoundException($"Record '{reference.Key}' does not exist.");
            var changes = new List<IndexChange>();
            StageSemantic(search, reference, changes);
            _storage.Commit(changes);
            return true;
        });
    }

    private void StageSemantic(PreparedSearch search, StoredReference reference, List<IndexChange> changes)
    {
        if (!ReferenceEquals(search.Owner, this)) throw new ArgumentException("The search index belongs to another database.");
        changes.Add(new(search.File, SemanticBucket, ReferenceId(reference), search.Model, search.Vector, search.Text, FullText: true));
        changes.Add(new(FolderStorage.Records, SemanticMembership(reference), Id(search.File), Value: []));
    }

    internal bool RemoveSemantic(string name, RecordReference record)
    {
        var reference = Reference(record);
        var file = FolderStorage.IndexFile(name);
        return Access(() =>
        {
            var membership = SemanticMembership(reference);
            if (_storage.Lookup(FolderStorage.Records, membership, Id(file)) is null) return false;
            _storage.Commit([new(file, SemanticBucket, ReferenceId(reference), FullText: true), new(FolderStorage.Records, membership, Id(file))]);
            return true;
        });
    }

    private void StageSemanticRemovals(StoredReference reference, List<IndexChange> changes)
    {
        var bucket = SemanticMembership(reference);
        var files = _storage.Scan(FolderStorage.Records, bucket);
        if (files.Count == 0) return;
        var id = ReferenceId(reference);
        foreach (var file in files) changes.Add(new(OrderedKey.ReadText(file.Id), SemanticBucket, id, FullText: true));
        changes.Add(new(FolderStorage.Records, bucket, null));
    }

    internal IReadOnlyList<SemanticHit> FindSemantic(string name, string? query, float[]? vector, int limit, IEmbedder? embedder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0) return [];
        var file = FolderStorage.IndexFile(name);
        var ftsQuery = query is null ? null : SearchText.ToMatchQuery(query);
        string model;
        lock (_embeddingGate) model = (embedder ?? _embedder).Model;
        var snapshot = Access(() =>
        {
            var modelKey = Id(model);
            var vectors = vector is not null || !string.IsNullOrWhiteSpace(query)
                ? _storage.ReadVectors(file, SemanticBucket, model, modelKey) : [];
            var words = ftsQuery is not null ? _storage.Search(file, ftsQuery, vectors.Count == 0 ? limit : int.MaxValue) : [];
            return (Vectors: vectors, Words: words);
        });

        var scores = new Dictionary<byte[], float>(ByteEqualityComparer.Instance);
        foreach (var hit in snapshot.Words) scores[hit.Id] = hit.Score;
        if (snapshot.Vectors.Count > 0)
        {
            var (queryModel, embedding) = Embed(query ?? string.Empty, vector, embedder);
            if (model == queryModel && embedding.Length > 0)
                foreach (var candidate in snapshot.Vectors)
                {
                    if (candidate.Value.Length != embedding.Length * sizeof(float)) continue;
                    ReadOnlySpan<float> candidateVector = BitConverter.IsLittleEndian
                        ? MemoryMarshal.Cast<byte, float>(candidate.Value) : VectorMath.FromBytes(candidate.Value);
                    var similarity = Math.Clamp(VectorMath.Dot(embedding, candidateVector), -1, 1);
                    if (similarity <= 0) continue;
                    scores.TryGetValue(candidate.Id, out var bm25);
                    scores[candidate.Id] = similarity + bm25;
                }
        }

        return scores.OrderByDescending(hit => hit.Value).ThenBy(hit => hit.Key, ByteOrderComparer.Instance).Take(limit)
            .Select(hit =>
            {
                var reference = JsonSerializer.Deserialize<StoredReference>(hit.Key)!;
                return new SemanticHit(this, reference.Type, reference.Key, hit.Value);
            }).ToArray();
    }

    internal bool HasVector(string name, RecordReference record, IEmbedder? embedder)
    {
        var reference = Reference(record);
        string modelName;
        lock (_embeddingGate) modelName = (embedder ?? _embedder).Model;
        var model = Id(modelName);
        return Access(() => _storage.HasSort(FolderStorage.IndexFile(name), SemanticBucket, ReferenceId(reference), model));
    }

    internal IReadOnlyList<SemanticHit> FindKeywords(string name, IEnumerable<KeywordTerm> terms, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var query = string.Join(" OR ", terms.Where(term => term.Text.Length > 0)
            .Select(term => "\"" + term.Text.Replace("\"", "\"\"") + "\"" + (term.Prefix ? "*" : "")));
        if (query.Length == 0 || limit == 0) return [];
        return Access(() => _storage.Search(FolderStorage.IndexFile(name), query, limit)
            .Select(hit =>
            {
                var reference = JsonSerializer.Deserialize<StoredReference>(hit.Id)!;
                return new SemanticHit(this, reference.Type, reference.Key, hit.Score);
            }).ToArray());
    }

    private (string Model, float[] Vector) Embed(string text, float[]? vector, IEmbedder? embedder)
    {
        lock (_embeddingGate)
        {
            var model = (embedder ?? _embedder).Model;
            return (model, Normalize(vector ?? (embedder ?? _embedder).Embed(text)));
        }
    }

    private static float[] Normalize(float[] vector)
    {
        double norm = 0;
        foreach (var component in vector)
        {
            if (!float.IsFinite(component)) throw new ArgumentException("Vector components must be finite.", nameof(vector));
            norm += (double)component * component;
        }
        if (norm == 0) return [];
        var scale = Math.Sqrt(norm);
        var unit = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) unit[i] = (float)(vector[i] / scale);
        return unit;
    }

    private sealed class ByteEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteEqualityComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            hash.AddBytes(value);
            return hash.ToHashCode();
        }
    }

    private sealed class ByteOrderComparer : IComparer<byte[]>
    {
        public static readonly ByteOrderComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
