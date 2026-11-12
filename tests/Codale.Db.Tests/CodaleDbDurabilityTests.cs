using Codale.Db;

namespace Codale.Db.Tests;

/// <summary>
/// What the log looks like after the process dies at an awkward moment, and whether the compact in-memory
/// structures still answer the way a freshly built database would.
/// </summary>
public sealed class CodaleDbDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private static IReadOnlyList<KeywordTerm> Words(params string[] words) => [.. words.Select(w => new KeywordTerm(w))];

    /// <summary>Copies the segment files as they are right now: the image a crash at this instant would leave.</summary>
    private static void Snapshot(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "seg-*.log"))
        {
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = File.Create(Path.Combine(to, Path.GetFileName(file)));
            source.CopyTo(target);
        }
    }

    [Fact]
    public void A_batch_interrupted_by_a_crash_leaves_nothing_behind()
    {
        using (var db = new CodaleDb(Dir("live")))
        {
            db.Open("kept").Write("before");
            db.Open("doc").Open("old").Index("body", ["survivor"]);
            db.Batch(() =>
            {
                db.Open("kept").Write("overwritten");
                db.Open("half").Write("written");
                db.Open("doc").Open("new").Index("body", ["phantom"]);
                db.Open("doc").Open("old").Remove();
                Snapshot(Dir("live"), Dir("crashed"));
            });
            Assert.Equal("overwritten", db.Open("kept").Read());
        }

        using var reopened = new CodaleDb(Dir("crashed"));
        Assert.Equal("before", reopened.Open("kept").Read());
        Assert.False(reopened.Open("half").Exists());
        Assert.Empty(reopened.Open("doc").Match("body", Words("phantom")));
        Assert.Equal(["old"], reopened.Open("doc").Match("body", Words("survivor")).Select(h => h.Text));

        // The cut is permanent and the database keeps working after it.
        reopened.Open("after").Write("fine");
        reopened.Dispose();
        using var again = new CodaleDb(Dir("crashed"));
        Assert.Equal("fine", again.Open("after").Read());
        Assert.Equal("before", again.Open("kept").Read());
    }

    [Fact]
    public void A_completed_batch_survives_and_an_empty_batch_writes_nothing()
    {
        long sizeBefore;
        using (var db = new CodaleDb(Dir("db")))
        {
            db.Batch(() =>
            {
                db.Open("a").Write("1");
                db.Batch(() => db.Open("b").Write("2")); // nested: still one unit
            });
            db.Flush();
            sizeBefore = Directory.EnumerateFiles(Dir("db"), "seg-*.log").Sum(f => new FileInfo(f).Length);
            db.Batch(() => _ = db.Open("a").Read());
            Assert.Equal(sizeBefore, Directory.EnumerateFiles(Dir("db"), "seg-*.log").Sum(f => new FileInfo(f).Length));
        }

        using var reopened = new CodaleDb(Dir("db"));
        Assert.Equal("1", reopened.Open("a").Read());
        Assert.Equal("2", reopened.Open("b").Read());
    }

    [Fact]
    public void A_batch_whose_action_throws_keeps_the_writes_made_before_the_exception()
    {
        using (var db = new CodaleDb(Dir("db")))
        {
            Assert.Throws<InvalidOperationException>(() => db.Batch(() =>
            {
                db.Open("a").Write("1");
                throw new InvalidOperationException("boom");
            }));
            db.Open("b").Write("2");
        }

        using var reopened = new CodaleDb(Dir("db"));
        Assert.Equal("1", reopened.Open("a").Read());
        Assert.Equal("2", reopened.Open("b").Read());
    }

    [Fact]
    public void A_torn_final_record_is_dropped_and_the_rest_is_intact()
    {
        using (var db = new CodaleDb(Dir("db")))
        {
            db.Open("a").Write("1");
            db.Open("b").Write(new string('x', 5000));
        }
        var segment = Directory.EnumerateFiles(Dir("db"), "seg-*.log").Single();
        using (var stream = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
            stream.SetLength(stream.Length - 100);

        using var reopened = new CodaleDb(Dir("db"));
        Assert.Equal("1", reopened.Open("a").Read());
        Assert.False(reopened.Open("b").Exists());
        reopened.Open("c").Write("3");
        Assert.Equal("3", reopened.Open("c").Read());
    }

    [Fact]
    public void A_crash_between_writing_the_compacted_log_and_deleting_the_old_one_loses_nothing()
    {
        // Small segments, so the old log spans several files.
        var options = new CodaleDbOptions { SegmentBytes = 4096, AutoCompact = false };
        using (var db = new CodaleDb(Dir("live"), options))
        {
            Fill(db);
            Snapshot(Dir("live"), Dir("crashed")); // the old segments
            db.Compact();
            Snapshot(Dir("live"), Dir("crashed")); // plus the new ones: nothing was deleted yet
            AssertFilled(db);
        }
        Assert.True(Directory.EnumerateFiles(Dir("crashed")).Count() > Directory.EnumerateFiles(Dir("live"), "seg-*.log").Count());

        using var reopened = new CodaleDb(Dir("crashed"), options);
        AssertFilled(reopened);
    }

    [Fact]
    public void A_crash_while_deleting_old_segments_does_not_bring_removed_data_back()
    {
        var options = new CodaleDbOptions { SegmentBytes = 4096, AutoCompact = false };
        using (var db = new CodaleDb(Dir("live"), options))
        {
            Fill(db);
            Snapshot(Dir("live"), Dir("crashed"));
            db.Compact();
            Snapshot(Dir("live"), Dir("crashed"));
        }
        // Old segments are deleted oldest first; stop after the first two.
        foreach (var file in Directory.EnumerateFiles(Dir("crashed")).Order().Take(2)) File.Delete(file);

        using var reopened = new CodaleDb(Dir("crashed"), options);
        AssertFilled(reopened);
    }

    /// <summary>A distinct made-up word per item, so each embedding has one clear nearest neighbour: itself.</summary>
    private static string Topic(int i) => string.Concat(Enumerable.Repeat(string.Concat($"{i * 7919 % 100003:D5}".Select(c => (char)('a' + (c - '0') * 2))), 2));

    private static void Fill(CodaleDb db)
    {
        var items = db.Open("item");
        for (var i = 0; i < 60; i++)
        {
            var node = items.Open($"n{i:D3}");
            node.Write($"value {i} " + new string('v', 150));
            node.Index("body", ["common", $"word{i}", i % 2 == 0 ? "even" : "odd"]);
            node.Embed(Topic(i));
        }
        for (var i = 0; i < 60; i += 3) items.Open($"n{i:D3}").Remove();
        for (var i = 1; i < 60; i += 3) items.Open($"n{i:D3}").Write($"rewritten {i} " + new string('w', 150));
        items.Open("n001").Link("next", items.Open("n002"));
        db.Open("blob").WriteBlob(new byte[3000]);
        db.Open("small").Write("tiny");
    }

    private static void AssertFilled(CodaleDb db)
    {
        var items = db.Open("item");
        Assert.Equal(40, items.ChildCount());
        Assert.False(items.Open("n000").Exists());
        Assert.StartsWith("rewritten 1 ", items.Open("n001").Read());
        Assert.StartsWith("value 2 ", items.Open("n002").Read());
        Assert.Empty(items.Match("body", Words("word0")));
        Assert.Equal(["n002"], items.Match("body", Words("word2")).Select(h => h.Text));
        Assert.Equal(40, items.Match("body", Words("common"), 100).Count);
        Assert.Equal("n005", items.Query(Topic(5), 1).Single().Text);
        Assert.Equal("n058", items.Query(Topic(58), 1).Single().Text);
        Assert.DoesNotContain("n003", items.Query(Topic(3), 100).Select(h => h.Text));
        Assert.Single(items.Open("n001").Links("next"));
        Assert.Equal(3000, db.Open("blob").ReadBlob()!.Length);
        Assert.Equal("tiny", db.Open("small").Read());
    }

    [Fact]
    public void Compaction_shrinks_the_log_and_a_reopen_sees_the_same_data()
    {
        var options = new CodaleDbOptions { AutoCompact = false };
        using (var db = new CodaleDb(Dir("db"), options))
        {
            Fill(db);
            for (var round = 0; round < 20; round++) db.Open("churn").Write(new string((char)('a' + round), 4000));
            db.Flush();
            var before = new DirectoryInfo(Dir("db")).EnumerateFiles("seg-*.log").Sum(f => f.Length);
            db.Compact();
            var after = new DirectoryInfo(Dir("db")).EnumerateFiles("seg-*.log").Sum(f => f.Length);
            Assert.True(after < before / 2, $"{before} -> {after}");
            AssertFilled(db);
        }

        using var reopened = new CodaleDb(Dir("db"), options);
        AssertFilled(reopened);
        reopened.Compact(); // compacting an already compact log moves every record again
        AssertFilled(reopened);
    }

    [Fact]
    public void Heavy_reindexing_sweeps_dead_postings_without_changing_what_matches()
    {
        using var churned = new CodaleDb(Dir("churned"));
        using var fresh = new CodaleDb(Dir("fresh"));
        var random = new Random(7);
        string[] vocabulary = [.. Enumerable.Range(0, 400).Select(i => $"w{i}")];
        List<string> Document() => [.. Enumerable.Range(0, 40).Select(_ => vocabulary[(int)(Math.Pow(random.NextDouble(), 2) * vocabulary.Length)])];

        var final = new Dictionary<string, List<string>>();
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < 300; i++)
            {
                var name = $"d{i:D3}";
                if (round > 0 && i % 7 == round)
                {
                    churned.Open("docs").Open(name).Remove();
                    final.Remove(name);
                    continue;
                }
                var words = Document();
                churned.Open("docs").Open(name).Index("body", words);
                final[name] = words;
            }
        }
        // Same documents, same arrival order, indexed once.
        foreach (var (name, words) in final.OrderBy(d => d.Key, StringComparer.Ordinal)) fresh.Open("docs").Open(name).Index("body", words);

        foreach (var query in new[] { Words("w0"), Words("w3", "w17", "w250"), Words("w399", "w1"), [new KeywordTerm("w1", Prefix: true)], [new KeywordTerm("w39", Prefix: true), new KeywordTerm("w5")] })
        {
            var expected = fresh.Open("docs").Match("body", query, 400);
            var actual = churned.Open("docs").Match("body", query, 400);
            Assert.Equal(expected.Count, actual.Count);
            // Scores are equal; documents with equal scores may be ordered differently (arrival order differs).
            Assert.Equal(expected.Select(h => (h.Text, h.Score)).OrderBy(h => h.Text, StringComparer.Ordinal), actual.Select(h => (h.Text, h.Score)).OrderBy(h => h.Text, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Prefix_terms_find_words_added_before_and_after_the_word_list_was_sorted()
    {
        using var db = new CodaleDb(Dir("db"));
        var docs = db.Open("docs");
        for (var i = 0; i < 3000; i++) docs.Open($"d{i}").Index("body", [$"filler{i}", i == 10 ? "connector" : "x"]);
        docs.Open("late").Index("body", ["connection"]);

        var hits = docs.Match("body", [new KeywordTerm("connect", Prefix: true)]);
        Assert.Equal(["d10", "late"], hits.Select(h => h.Text).Order(StringComparer.Ordinal));
        Assert.Equal(11, docs.Match("body", [new KeywordTerm("filler100", Prefix: true)], 50).Count);
    }

    [Fact]
    public void Keyword_statistics_cover_only_the_subtree_searched()
    {
        using var db = new CodaleDb(Dir("db"));
        db.Open("a").Open("one").Index("body", ["shared", "rare"]);
        db.Open("a").Open("two").Index("body", ["shared"]);
        for (var i = 0; i < 50; i++) db.Open("b").Open($"n{i}").Index("body", ["rare", "padding", "padding"]);

        // "rare" is rare under a (1 of 2) but in every document under b: its weight differs by subtree.
        var inA = db.Open("a").Match("body", Words("rare")).Single();
        var inB = db.Open("b").Match("body", Words("rare"), 100);
        Assert.Equal("one", inA.Text);
        Assert.Equal(50, inB.Count);
        Assert.True(inA.Score > inB[0].Score);
        Assert.Equal(["one", "two"], db.Open("a").Match("body", Words("shared")).Select(h => h.Text).Order());
    }

    [Fact]
    public void Vector_dot_product_matches_the_plain_loop()
    {
        var random = new Random(3);
        foreach (var length in new[] { 1, 7, 8, 9, 384, 387 })
        {
            var row = new byte[length];
            random.NextBytes(row);
            var query = new float[length];
            for (var i = 0; i < length; i++) query[i] = (float)(random.NextDouble() * 2 - 1);
            var expected = 0f;
            for (var i = 0; i < length; i++) expected += (sbyte)row[i] * query[i];
            Assert.Equal(expected, CodaleDb.Dot(row, query), 0.01f);
        }
    }

    [Fact]
    public void Removing_and_rewriting_a_subtree_many_times_keeps_search_and_reads_consistent()
    {
        var options = new CodaleDbOptions { CompactMinDeadBytes = 16 << 10, SegmentBytes = 32 << 10 };
        using (var db = new CodaleDb(Dir("db"), options))
        {
            for (var round = 0; round < 40; round++)
            {
                db.Batch(() =>
                {
                    db.Open("file").Remove();
                    for (var i = 0; i < 25; i++)
                    {
                        var node = db.Open("file").Open($"c{i}");
                        node.Write($"round {round} chunk {i} " + new string('z', 200));
                        node.Index("body", ["chunk", $"round{round}", $"c{i}"]);
                        node.Embed($"round {round} chunk {i}");
                    }
                });
            }
            Check(db);
        }
        using var reopened = new CodaleDb(Dir("db"), options);
        Check(reopened);

        static void Check(CodaleDb db)
        {
            Assert.Equal(25, db.Open("file").ChildCount());
            Assert.StartsWith("round 39 chunk 4 ", db.Open("file").Open("c4").Read());
            Assert.Equal(25, db.Open("file").Match("body", Words("round39"), 100).Count);
            Assert.Empty(db.Open("file").Match("body", Words("round38"), 100));
            Assert.Equal(25, db.Open("file").Query("round 39 chunk", 100).Count);
        }
    }
}
