using Codale.Core.Features;

namespace Codale.Search.Tests;

public sealed class QuantizedMatrixTests
{
    [Fact]
    public void Quantized_matrix_ranks_like_float_dot_products()
    {
        var e = HashedTextEmbedder.Instance;
        string[] docs = ["connector table sql", "revenue chart graph", "login oauth redirect", "connectors drawer ui"];
        var matrix = QuantizedMatrix.From(docs.Select((d, i) => ((long)i, VectorMath.Quantize(e.Embed(d)))).ToList());
        var query = e.Embed("connector");
        var top = matrix.Top(query, 2);
        Assert.Equal(2, top.Count);
        var expected = docs.Select((d, i) => (i, VectorMath.Dot(query, e.Embed(d)))).OrderByDescending(x => x.Item2).Take(2).ToList();
        Assert.Equal(expected.Select(x => (long)x.i), top.Select(t => t.Id));
        Assert.InRange(top[0].Score, expected[0].Item2 - 0.02f, expected[0].Item2 + 0.02f);
    }

    [Fact]
    public void A_query_of_another_dimension_matches_nothing()
    {
        var matrix = QuantizedMatrix.From([(1L, VectorMath.Quantize([1f, 0f, 0f]))]);
        Assert.Empty(matrix.Top([1f, 0f], 5));
    }
}
