using Codale.Db;

namespace Codale.Db.Tests;

public sealed class CodaleDbSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codale-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CodaleDb NewDb(CodaleDbOptions? options = null) => new(_dir, options);

    private static IReadOnlyList<KeywordTerm> Words(params string[] words) => [.. words.Select(w => new KeywordTerm(w))];

    [Fact]
    public void Keyword_match_ranks_the_document_with_the_rarer_and_more_frequent_word_first()
    {
        using var db = NewDb();
        var docs = db.Open("docs");
        docs.Open("a").Index("body", ["alpha", "beta", "gamma"]);
        docs.Open("b").Index("body", ["alpha", "alpha", "alpha", "delta"]);
        docs.Open("c").Index("body", ["beta", "gamma", "gamma"]);

        var hits = docs.Match("body", Words("alpha", "delta"));

        Assert.Equal(["b", "a"], hits.Select(h => h.Text));
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Keyword_match_supports_prefix_terms()
    {
        using var db = NewDb();
        var docs = db.Open("docs");
        docs.Open("a").Index("body", ["connector", "table"]);
        docs.Open("b").Index("body", ["unrelated"]);

        Assert.Empty(docs.Match("body", Words("connect")));
        Assert.Equal(["a"], docs.Match("body", [new KeywordTerm("connect", Prefix: true)]).Select(h => h.Text));
    }

    [Fact]
    public void Keyword_match_is_limited_to_the_subtree_it_is_asked_on()
    {
        using var db = NewDb();
        db.Open("one").Open("x").Index("body", ["shared"]);
        db.Open("two").Open("y").Index("body", ["shared"]);

        Assert.Equal(["x"], db.Open("one").Match("body", Words("shared")).Select(h => h.Text));
        Assert.Equal(2, db.Open("one").Match("body", Words("shared")).Count + db.Open("two").Match("body", Words("shared")).Count);
    }

    [Fact]
    public void Fields_are_separate_corpora()
    {
        using var db = NewDb();
        var node = db.Open("n");
        node.Index("title", ["cat"]);
        node.Index("body", ["dog"]);

        Assert.Single(node.Match("title", Words("cat")));
        Assert.Empty(node.Match("title", Words("dog")));
    }

    [Fact]
    public void Reindexing_replaces_the_words_and_removing_the_node_drops_the_document()
    {
        using (var db = NewDb())
        {
            var node = db.Open("d").Open("n");
            node.Index("body", ["old"]);
            node.Index("body", ["new"]);
            Assert.Empty(db.Open("d").Match("body", Words("old")));
            Assert.Single(db.Open("d").Match("body", Words("new")));

            db.Open("d").Open("gone").Index("body", ["new"]);
            db.Open("d").Open("gone").Remove();
            Assert.Single(db.Open("d").Match("body", Words("new")));
        }

        using var reopened = NewDb();
        Assert.Empty(reopened.Open("d").Match("body", Words("old")));
        Assert.Equal(["n"], reopened.Open("d").Match("body", Words("new")).Select(h => h.Text));
    }

    [Fact]
    public void Keyword_documents_survive_compaction()
    {
        using var db = NewDb(new CodaleDbOptions { AutoCompact = false });
        for (var i = 0; i < 50; i++) db.Open("d").Open("n" + i).Index("body", ["word" + i, "common"]);
        db.Open("d").Open("n3").Remove();

        db.Compact();

        Assert.Equal(49, db.Open("d").Match("body", Words("common"), 100).Count);
        Assert.Equal(["n7"], db.Open("d").Match("body", Words("word7")).Select(h => h.Text));
    }

    [Fact]
    public void An_embedding_can_be_attached_to_a_node_with_its_own_value_and_is_found_by_query()
    {
        using var db = NewDb();
        var chunks = db.Open("chunks");
        chunks.Open("1").Write("{\"id\":1}");
        chunks.Open("1").Embed("select the database path");
        chunks.Open("2").Write("{\"id\":2}");
        chunks.Open("2").Embed("enable yolo mode");

        Assert.True(chunks.Open("1").HasVector());
        Assert.False(db.Open("chunks").Open("3").HasVector());
        var hit = chunks.Query("database path")[0];
        Assert.Equal("1", hit.Node.Name);
        Assert.Equal("{\"id\":1}", hit.Node.Read());
    }

    [Fact]
    public void Rewriting_a_value_keeps_its_embedding()
    {
        using var db = NewDb();
        var node = db.Open("c").Open("1");
        node.Write("first");
        node.Embed("database path");
        node.Write("second");

        Assert.Equal("second", db.Open("c").Query("database path")[0].Node.Read());
    }

    [Fact]
    public void Embeddings_from_another_model_are_ignored_until_dropped_and_replaced()
    {
        using (var db = NewDb(new CodaleDbOptions { Embedder = new Tagged("old") }))
        {
            db.Open("c").Open("1").Write("x");
            db.Open("c").Open("1").Embed("database path");
        }

        using var reopened = NewDb(new CodaleDbOptions { Embedder = new Tagged("new") });
        Assert.False(reopened.Open("c").Open("1").HasVector());
        Assert.Empty(reopened.Open("c").Query("database path"));

        reopened.Open("c").DropStaleVectors();
        reopened.Open("c").Open("1").Embed("database path");
        Assert.Single(reopened.Open("c").Query("database path"));
    }

    [Fact]
    public void Json_values_round_trip()
    {
        using var db = NewDb();
        db.Open("p").WriteJson(new Point(3, "x"));
        Assert.Equal(new Point(3, "x"), db.Open("p").ReadJson<Point>());
        Assert.Null(db.Open("missing").ReadJson<Point>());
    }

    [Fact]
    public void Descendants_lists_every_valued_node_below_and_child_count_counts_direct_children()
    {
        using var db = NewDb();
        db.Open("a").Open("b").Write("1");
        db.Open("a").Open("c").Open("d").Write("2");
        db.Open("a").Open("c").Write("3");

        Assert.Equal(["a.b", "a.c", "a.c.d"], db.Open("a").Descendants(sorted: true).Select(n => n.ToString()));
        Assert.Equal(2, db.Open("a").ChildCount());
        Assert.Equal(0, db.Open("none").ChildCount());
    }

    [Fact]
    public void A_batch_is_isolated_from_other_writers_and_commits_in_order()
    {
        using var db = NewDb();
        var counter = db.Open("counter");
        counter.Write("0");

        Parallel.For(0, 8, _ => db.Batch(() =>
        {
            for (var i = 0; i < 50; i++) counter.Write((int.Parse(counter.Read()!) + 1).ToString());
        }));

        Assert.Equal("400", counter.Read());
    }

    [Fact]
    public void Acquire_shares_one_instance_until_the_last_lease_is_released()
    {
        var first = CodaleDb.Acquire(_dir);
        var second = CodaleDb.Acquire(_dir);
        Assert.Same(first, second);

        first.Open("k").Write("v");
        first.Dispose();
        Assert.Equal("v", second.Open("k").Read());
        second.Dispose();

        using var again = CodaleDb.Acquire(_dir);
        Assert.NotSame(first, again);
        Assert.Equal("v", again.Open("k").Read());
    }

    [Fact]
    public void A_lock_timeout_waits_for_the_other_holder_to_close()
    {
        var holder = new CodaleDb(_dir);
        Assert.Throws<IOException>(() => new CodaleDb(_dir));

        _ = Task.Run(() =>
        {
            Thread.Sleep(150);
            holder.Dispose();
        });
        using var waited = new CodaleDb(_dir, new CodaleDbOptions { LockTimeout = TimeSpan.FromSeconds(5) });
        waited.Open("ok").Write("1");
    }

    private sealed record Point(int X, string Label);

    private sealed class Tagged(string model) : IEmbedder
    {
        public string Model => model;

        public float[] Embed(string text) => Codale.Core.Features.HashedTextEmbedder.Instance.Embed(text);
    }
}
