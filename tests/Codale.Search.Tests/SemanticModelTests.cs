using System.Diagnostics;
using Codale.Core.Features;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// Parity with the reference implementation (Python model2vec 0.x, potion-base-8M) and cost checks.
/// Uses the bundled copy (tools\models, linked into the test output like rg.exe), so tests never need the network.
/// </summary>
public sealed class SemanticModelTests(ITestOutputHelper output)
{
    private static readonly string[] Sentences =
    [
        "where does the new connector table located?",
        "ensure connectors table create table sql",
        "render revenue chart graph",
        "fix the login redirect after oauth",
        "how is the connector secret encrypted?",
    ];

    // model2vec StaticModel.encode(Sentences) @ encode(Sentences).T, rounded to 3 places.
    private static readonly float[,] Reference =
    {
        { 1f, 0.607f, 0.12f, 0.233f, 0.202f },
        { 0.607f, 1f, 0.172f, 0.127f, 0.124f },
        { 0.12f, 0.172f, 1f, 0.115f, -0.051f },
        { 0.233f, 0.127f, 0.115f, 1f, 0.166f },
        { 0.202f, 0.124f, -0.051f, 0.166f, 1f },
    };

    private static Task<StaticEmbedder?> ModelAsync() =>
        Task.FromResult<StaticEmbedder?>(SemanticModel.Load(SemanticModel.BundledDirectory));

    [Fact]
    public async Task Embeddings_match_the_reference_model2vec_implementation()
    {
        if (await ModelAsync() is not { } model) return;
        var first = model.Embed(Sentences[0]);
        // First dimensions of the reference vector, tolerance covers int8 quantisation.
        float[] expected = [0.16196f, 0.23099f, -0.10709f, -0.12912f, -0.07481f, 0.04614f];
        for (var i = 0; i < expected.Length; i++) Assert.InRange(first[i], expected[i] - 0.01f, expected[i] + 0.01f);
        var vectors = Sentences.Select(model.Embed).ToArray();
        for (var i = 0; i < Sentences.Length; i++)
        for (var j = 0; j < Sentences.Length; j++)
            Assert.InRange(VectorMath.Dot(vectors[i], vectors[j]), Reference[i, j] - 0.02f, Reference[i, j] + 0.02f);
    }

    [Fact]
    public async Task Model_is_small_in_memory_and_fast_on_cpu()
    {
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();
        if (await ModelAsync() is not { } model) return;
        var load = timer.ElapsedMilliseconds;
        var resident = GC.GetTotalMemory(true) - before;
        var chunk = string.Join("\n", Enumerable.Repeat("export async function ensureConnectorsTable(db: D1Database) { await db.exec(createTableSql); }", 12));
        model.Embed(chunk);
        timer.Restart();
        const int Chunks = 2000;
        for (var i = 0; i < Chunks; i++) model.Embed(chunk);
        var perChunk = timer.Elapsed.TotalMilliseconds / Chunks;
        output.WriteLine($"load {load} ms, managed memory {resident / 1024 / 1024.0:F1} MB, {perChunk * 1000:F0} µs per 1.4 KB chunk");
        GC.KeepAlive(model);
        Assert.True(resident < 20 * 1024 * 1024, $"model holds {resident} bytes");
        Assert.True(perChunk < 2, $"{perChunk} ms per chunk");
    }

    [Fact]
    public async Task Semantic_model_tells_topic_changes_from_follow_ups()
    {
        if (await ModelAsync() is not { } model) return;
        string[] earlier = ["where does the new connector table located?"];
        Assert.False(TopicShift.IsNewTopic("how is the connector secret encrypted?", earlier, model));
        Assert.False(TopicShift.IsNewTopic("also add an index to it", earlier, model));
        Assert.True(TopicShift.IsNewTopic("the terminal flickers when resizing the window", earlier, model));
        Assert.True(TopicShift.IsNewTopic("fix the login redirect after oauth", earlier, model));
        Assert.False(TopicShift.IsNewTopic("fix the oauth callback", ["investigate the failure"], model,
            "The login failure is in the oauth callback: it discards the redirect URI."));
        Assert.True(TopicShift.IsNewTopic("the terminal flickers when resizing", ["investigate the failure"], model,
            "The login failure is in the oauth callback: it discards the redirect URI."));
    }

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
}
