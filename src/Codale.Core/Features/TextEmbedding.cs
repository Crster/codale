using System.Buffers.Binary;
using System.Text;

namespace Codale.Core.Features;

/// <summary>What a text is embedded as: models trained with task prefixes embed a search query apart from the text it should find.</summary>
public enum EmbedRole
{
    Document,
    Query,
}

/// <summary>
/// Turns text into a dense, L2-normalised vector for similarity search. <see cref="Model"/> is stored
/// beside every vector, so swapping the implementation re-embeds the index.
/// </summary>
public interface ITextEmbedder
{
    string Model { get; }

    /// <summary>The cosine above which two short texts are on one subject, for this model.</summary>
    float RelatedSimilarity { get; }

    /// <summary>The cosine a neighbour needs to count as a match on its own, without lexical evidence beside it.</summary>
    float MatchSimilarity { get; }

    /// <summary>One unit vector per text, in order.</summary>
    /// <exception cref="Helper.HelperModelException">A model behind a server could not be reached or answered badly.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbedRole role, CancellationToken ct = default);
}

/// <summary>
/// Local, dependency-free embedding: identifier words, their light stems and their character
/// trigrams are hashed into a fixed number of signed buckets (the "hashing trick", as in fastText
/// subword vectors). It captures what exact-token BM25 misses — plurals and inflections
/// ("connectors"/"connector", "located"/"location"), identifier fragments ("connTable"), typos —
/// while staying instant and private. It does not know synonyms; that needs a trained model.
/// </summary>
public sealed class HashedTextEmbedder : ITextEmbedder
{
    public static readonly HashedTextEmbedder Instance = new();

    public string Model => "hashed-ngram-v1-384";
    public int Dimension => 384;
    public float RelatedSimilarity => 0.25f;
    public float MatchSimilarity => 0.25f;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbedRole role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToArray());

    public float[] Embed(string text)
    {
        var vector = new float[Dimension];
        foreach (var token in SearchText.Tokens(text))
        {
            if (token.Length < 2 || SearchText.IsStopWord(token)) continue;
            var stem = SearchText.Stem(token);
            Add(vector, "w:" + token, 1.0f);
            if (stem != token) Add(vector, "w:" + stem, 0.8f);
            if (stem.Length < 3 || char.IsDigit(stem[0])) continue;
            var padded = "<" + stem + ">";
            for (var i = 0; i + 3 <= padded.Length; i++) Add(vector, "g:" + padded.Substring(i, 3), 0.35f);
        }
        // Sublinear term frequency: a word repeated forty times in a chunk must not drown the rest.
        double norm = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = MathF.Sign(vector[i]) * MathF.Log(1 + MathF.Abs(vector[i]));
            norm += vector[i] * vector[i];
        }
        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < vector.Length; i++) vector[i] *= scale;
        }
        return vector;
    }

    private void Add(float[] vector, string feature, float weight)
    {
        // FNV-1a: stable across processes and runtimes, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(feature)) hash = (hash ^ b) * 16777619u;
        vector[hash % (uint)Dimension] += (hash & 0x8000_0000) == 0 ? weight : -weight;
    }
}

public static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var length = Math.Min(a.Length, b.Length);
        var sum = System.Numerics.Vector<float>.Zero;
        var i = 0;
        for (; i <= length - System.Numerics.Vector<float>.Count; i += System.Numerics.Vector<float>.Count)
            sum += new System.Numerics.Vector<float>(a[i..]) * new System.Numerics.Vector<float>(b[i..]);
        var total = System.Numerics.Vector.Sum(sum);
        for (; i < length; i++) total += a[i] * b[i];
        return total;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / 4];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return vector;
    }

    /// <summary>A unit vector as int8 (×127): a quarter of float32's size, cosine error under 0.01.</summary>
    public static byte[] Quantize(float[] unit)
    {
        var bytes = new byte[unit.Length];
        for (var i = 0; i < unit.Length; i++) bytes[i] = (byte)(sbyte)Math.Clamp(MathF.Round(unit[i] * 127f), -127, 127);
        return bytes;
    }
}

/// <summary>
/// Chunk vectors of one worktree and model, packed as int8 rows in one array: no object per row,
/// ~256 bytes a chunk, and a scan of 10k chunks takes about a millisecond.
/// </summary>
public sealed class QuantizedMatrix
{
    public static readonly QuantizedMatrix Empty = new([], [], 0);

    private readonly long[] _ids;
    private readonly sbyte[] _data;
    private readonly int _dimension;

    public QuantizedMatrix(long[] ids, sbyte[] data, int dimension)
    {
        _ids = ids;
        _data = data;
        _dimension = dimension;
    }

    public int Count => _ids.Length;

    public static QuantizedMatrix From(IReadOnlyList<(long Id, byte[] Vector)> rows)
    {
        if (rows.Count == 0) return Empty;
        var dimension = rows[0].Vector.Length;
        var ids = new long[rows.Count];
        var data = new sbyte[rows.Count * dimension];
        var kept = 0;
        foreach (var (id, vector) in rows)
        {
            if (vector.Length != dimension) continue;
            ids[kept] = id;
            Buffer.BlockCopy(vector, 0, data, kept * dimension, dimension);
            kept++;
        }
        return kept == rows.Count ? new QuantizedMatrix(ids, data, dimension) : new QuantizedMatrix(ids[..kept], data[..(kept * dimension)], dimension);
    }

    /// <summary>The <paramref name="k"/> rows most similar to <paramref name="query"/> (a unit vector), best first.</summary>
    public IReadOnlyList<(long Id, float Score)> Top(float[] query, int k)
    {
        if (Count == 0 || query.Length != _dimension) return [];
        var best = new PriorityQueue<long, float>(k + 1);
        for (var row = 0; row < _ids.Length; row++)
        {
            var offset = row * _dimension;
            var dot = 0f;
            for (var d = 0; d < _dimension; d++) dot += _data[offset + d] * query[d];
            best.Enqueue(_ids[row], dot);
            if (best.Count > k) best.Dequeue();
        }
        var result = new List<(long, float)>(best.Count);
        while (best.TryDequeue(out var id, out var score)) result.Add((id, score / 127f));
        result.Reverse();
        return result;
    }
}
