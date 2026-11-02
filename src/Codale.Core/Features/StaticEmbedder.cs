using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codale.Core.Features;

/// <summary>
/// A Model2Vec static embedding model (e.g. minishlab/potion-base-8M): a BERT WordPiece tokenizer and
/// one learned vector per token. Encoding is a table lookup and a mean, with no transformer and no native
/// runtime, so it costs microseconds per text. The table is held as int8 with one scale per row:
/// 29.5k × 256 bytes ≈ 7.5 MB instead of 30 MB of float32, with no measurable change in ranking.
/// </summary>
public sealed class StaticEmbedder : ITextEmbedder
{
    private const int MaxTokens = 512;
    private const int MaxWordChars = 100;
    private readonly Dictionary<string, int> _vocab;
    private readonly sbyte[] _table;
    private readonly float[] _scales;
    private readonly int _unk;

    public string Model { get; }
    public int Dimension { get; }

    /// <summary>Static embeddings of two prompts on one subject commonly score 0.2–0.6; unrelated ones under 0.15.</summary>
    public float RelatedSimilarity => 0.3f;

    private StaticEmbedder(string model, Dictionary<string, int> vocab, sbyte[] table, float[] scales, int dimension)
    {
        Model = model;
        _vocab = vocab;
        _table = table;
        _scales = scales;
        Dimension = dimension;
        _unk = vocab.GetValueOrDefault("[UNK]", -1);
    }

    /// <summary>Loads <c>vocab.txt</c> (one token per line, id = line) and a safetensors file holding <c>embeddings</c> [vocab, dim] F32.</summary>
    public static StaticEmbedder Load(string model, string vocabPath, string safetensorsPath)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        foreach (var line in File.ReadLines(vocabPath, Encoding.UTF8)) vocab.TryAdd(line, id++);

        using var stream = File.OpenRead(safetensorsPath);
        Span<byte> lengthBytes = stackalloc byte[8];
        stream.ReadExactly(lengthBytes);
        var headerLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(lengthBytes));
        var header = new byte[headerLength];
        stream.ReadExactly(header);
        using var json = JsonDocument.Parse(header);
        var tensor = json.RootElement.GetProperty("embeddings");
        if (tensor.GetProperty("dtype").GetString() != "F32") throw new InvalidDataException("Embedding table must be F32.");
        var shape = tensor.GetProperty("shape");
        int rows = shape[0].GetInt32(), dimension = shape[1].GetInt32();
        if (rows != vocab.Count) throw new InvalidDataException($"Vocabulary has {vocab.Count} tokens but the table has {rows} rows.");
        stream.Position = 8 + headerLength + tensor.GetProperty("data_offsets")[0].GetInt64();

        var table = new sbyte[rows * dimension];
        var scales = new float[rows];
        var row = new byte[dimension * 4];
        var values = new float[dimension];
        for (var r = 0; r < rows; r++)
        {
            stream.ReadExactly(row);
            var max = 0f;
            for (var d = 0; d < dimension; d++)
            {
                values[d] = BinaryPrimitives.ReadSingleLittleEndian(row.AsSpan(d * 4));
                max = Math.Max(max, Math.Abs(values[d]));
            }
            var scale = max > 0 ? max / 127f : 1f;
            scales[r] = scale;
            for (var d = 0; d < dimension; d++) table[r * dimension + d] = (sbyte)Math.Clamp(MathF.Round(values[d] / scale), -127, 127);
        }
        return new StaticEmbedder(model, vocab, table, scales, dimension);
    }

    public float[] Embed(string text)
    {
        var sum = new float[Dimension];
        var count = 0;
        foreach (var id in Tokenize(text))
        {
            if (id == _unk) continue;
            var scale = _scales[id];
            var offset = id * Dimension;
            for (var d = 0; d < sum.Length; d++) sum[d] += _table[offset + d] * scale;
            if (++count >= MaxTokens) break;
        }
        // Mean then L2-normalise; the mean's divisor cancels out in the normalisation.
        double norm = 0;
        foreach (var v in sum) norm += v * v;
        if (norm > 0)
        {
            var inverse = (float)(1 / Math.Sqrt(norm));
            for (var d = 0; d < sum.Length; d++) sum[d] *= inverse;
        }
        return sum;
    }

    /// <summary>
    /// BERT uncased tokenization (clean, CJK spacing, lowercase, strip accents, split on whitespace and
    /// punctuation, greedy WordPiece). Identifiers are split at camelCase and digit boundaries first, so
    /// "ensureConnectorsTable" reads as "ensure connectors table" rather than sub-word noise.
    /// </summary>
    public IEnumerable<int> Tokenize(string text)
    {
        foreach (var word in BasicTokens(SplitIdentifiers(text)))
        {
            if (word.Length > MaxWordChars) { yield return _unk; continue; }
            var pieces = new List<int>();
            var start = 0;
            var bad = false;
            while (start < word.Length)
            {
                var end = word.Length;
                var found = -1;
                while (start < end)
                {
                    var piece = start > 0 ? "##" + word[start..end] : word[start..end];
                    if (_vocab.TryGetValue(piece, out var pieceId)) { found = pieceId; break; }
                    end--;
                }
                if (found < 0) { bad = true; break; }
                pieces.Add(found);
                start = end;
            }
            if (bad) { yield return _unk; continue; }
            foreach (var piece in pieces) yield return piece;
        }
    }

    private static string SplitIdentifiers(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0)
            {
                var p = text[i - 1];
                var boundary = (char.IsLower(p) && char.IsUpper(c)) ||
                    (char.IsLetter(p) && char.IsDigit(c)) || (char.IsDigit(p) && char.IsLetter(c)) ||
                    (char.IsUpper(p) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1]));
                if (boundary) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        var word = new StringBuilder();
        foreach (var raw in text)
        {
            if (raw == 0 || raw == 0xFFFD || (char.IsControl(raw) && raw is not ('\t' or '\n' or '\r'))) continue;
            if (char.IsWhiteSpace(raw))
            {
                if (word.Length > 0) { yield return Normalize(word); word.Clear(); }
                continue;
            }
            if (IsPunctuation(raw) || IsCjk(raw))
            {
                if (word.Length > 0) { yield return Normalize(word); word.Clear(); }
                yield return Normalize(new StringBuilder().Append(raw));
                continue;
            }
            word.Append(raw);
        }
        if (word.Length > 0) yield return Normalize(word);
    }

    private static string Normalize(StringBuilder word)
    {
        var lower = word.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsPunctuation(char c) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    private static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿';
}
