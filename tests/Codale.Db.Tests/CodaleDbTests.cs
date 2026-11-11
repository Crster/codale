using System.Security.Cryptography;
using System.Text;

using Codale.Db;

namespace Codale.Db.Tests;

public sealed class CodaleDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codale-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CodaleDb NewDb(CodaleDbOptions? options = null) => new(_dir, options);

    private static void Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(condition());
    }

    [Fact]
    public void A_nested_value_round_trips_and_survives_reopening()
    {
        using (var db = NewDb())
        {
            db.Open("setting").Open("ai provider").Open("claude KEY").Write("xxxxx");
            Assert.Equal("xxxxx", db.Open("setting").Open("ai provider").Open("claude KEY").Read());
            Assert.Null(db.Open("setting").Open("nothing").Read());
        }

        using var reopened = NewDb();
        Assert.Equal("xxxxx", reopened.Open("setting").Open("ai provider").Open("claude KEY").Read());
    }

    [Fact]
    public void Removing_a_node_clears_every_key_beneath_it()
    {
        using (var db = NewDb())
        {
            var provider = db.Open("setting").Open("ai provider");
            provider.Open("claude").Write("a");
            provider.Open("copilot").Write("b");
            db.Open("setting").Open("other").Write("keep");

            Assert.True(provider.Remove());
            Assert.Null(provider.Open("claude").Read());
            Assert.Empty(provider.Find());
            Assert.Equal("keep", db.Open("setting").Open("other").Read());
            Assert.False(provider.Remove());
        }

        using var reopened = NewDb();
        Assert.Null(reopened.Open("setting").Open("ai provider").Open("claude").Read());
        Assert.Equal("keep", reopened.Open("setting").Open("other").Read());
    }

    [Fact]
    public void Overwriting_keeps_only_the_latest_value()
    {
        using var db = NewDb();
        var node = db.Open("a").Open("b");
        node.Write("one");
        node.Write("two");
        Assert.Equal("two", node.Read());
    }

    [Fact]
    public void A_torn_tail_record_is_dropped_on_reopen_and_earlier_data_survives()
    {
        using (var db = NewDb())
        {
            db.Open("k").Write("first");
            db.Open("k2").Write("second");
        }
        var segment = Directory.GetFiles(_dir, "seg-*.log").Single();
        using (var stream = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
            stream.SetLength(stream.Length - 3);

        using var reopened = NewDb();
        Assert.Equal("first", reopened.Open("k").Read());
        Assert.Null(reopened.Open("k2").Read());
        reopened.Open("k3").Write("after");
        Assert.Equal("after", reopened.Open("k3").Read());
    }

    [Fact]
    public void Compaction_keeps_live_data_and_shrinks_the_log()
    {
        using var db = NewDb(new CodaleDbOptions { AutoCompact = false });
        var node = db.Open("big");
        for (var i = 0; i < 200; i++) node.Write(new string('x', 2000) + i);
        db.Open("gone").Open("x").Write("y");
        db.Open("gone").Remove();
        db.Open("keep").Write("alive");
        var before = Directory.GetFiles(_dir, "seg-*.log").Sum(f => new FileInfo(f).Length);

        db.Compact();

        var after = Directory.GetFiles(_dir, "seg-*.log").Sum(f => new FileInfo(f).Length);
        Assert.True(after < before / 10, $"{after} should be far below {before}");
        Assert.Equal(new string('x', 2000) + 199, node.Read());
        Assert.Equal("alive", db.Open("keep").Read());
        Assert.Null(db.Open("gone").Open("x").Read());

        db.Dispose();
        using var reopened = NewDb();
        Assert.Equal("alive", reopened.Open("keep").Read());
        Assert.Equal(new string('x', 2000) + 199, reopened.Open("big").Read());
    }

    [Fact]
    public void Large_values_roll_across_segments_and_read_back()
    {
        using (var db = NewDb(new CodaleDbOptions { SegmentBytes = 4096, AutoCompact = false }))
        {
            for (var i = 0; i < 50; i++) db.Open("n").Open(i.ToString()).Write(new string((char)('a' + i % 26), 3000));
        }
        Assert.True(Directory.GetFiles(_dir, "seg-*.log").Length > 1);
        using var reopened = NewDb();
        for (var i = 0; i < 50; i++) Assert.Equal(new string((char)('a' + i % 26), 3000), reopened.Open("n").Open(i.ToString()).Read());
    }

    [Fact]
    public void Concurrent_writers_do_not_lose_updates()
    {
        using var db = NewDb();
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 200; i++) db.Open("c").Open($"{t}-{i}").Write($"v{t}-{i}");
        });
        Assert.Equal(1600, db.Open("c").Find().Count);
        Assert.Equal("v3-17", db.Open("c").Open("3-17").Read());
    }

    [Fact]
    public void A_second_instance_on_the_same_directory_is_refused()
    {
        using var db = NewDb();
        Assert.Throws<IOException>(() => NewDb());
    }

    [Fact]
    public void Find_lists_values_beneath_a_node_and_filters_by_substring()
    {
        using var db = NewDb();
        var provider = db.Open("setting").Open("ai provider");
        provider.Open("Claude API key").Write("a");
        provider.Open("Copilot API key").Write("b");
        provider.Open("Local").Open("model").Write("c");

        var all = provider.Find();
        Assert.Equal(["setting.\"ai provider\".\"Claude API key\"", "setting.\"ai provider\".\"Copilot API key\"", "setting.\"ai provider\".Local.model"],
            all.Select(p => p.ToString()));
        Assert.Equal(["setting.\"ai provider\".\"Claude API key\""], provider.Find("claude").Select(p => p.ToString()));
    }

    [Fact]
    public void Query_returns_the_entry_closest_to_the_question()
    {
        using var db = NewDb();
        var description = db.Open("setting").Open("description");
        description.Write([
            "Define providers here to allow switching later",
            "Select the database path",
            "You can enable YOLO mode to again full access",
        ]);

        var hits = db.Open("setting").Query("choose database path");

        Assert.Equal("setting.description.\"Select the database path\"", hits[0].Path.ToString());
        Assert.Equal("Select the database path", hits[0].Text);
        Assert.InRange(hits[0].Score, 0.25f, 1.01f);
    }

    [Fact]
    public void Semantic_entries_persist_and_a_removed_entry_stops_matching()
    {
        using (var db = NewDb())
        {
            db.Open("d").Write(["Select the database path", "Enable YOLO mode"]);
        }
        using var reopened = NewDb();
        Assert.Equal("Select the database path", reopened.Open("d").Query("database path")[0].Text);

        reopened.Open("d").Open("Select the database path").Remove();
        Assert.DoesNotContain(reopened.Open("d").Query("database path"), h => h.Text == "Select the database path");
    }

    [Fact]
    public void Entries_embedded_by_another_model_are_re_embedded_on_first_query()
    {
        using (var db = NewDb(new CodaleDbOptions { Embedder = new TaggedEmbedder("old") }))
            db.Open("d").Write(["Select the database path"]);

        using var reopened = NewDb(new CodaleDbOptions { Embedder = new TaggedEmbedder("new") });
        Assert.Equal("Select the database path", reopened.Open("d").Query("database path")[0].Text);
    }

    [Fact]
    public void Links_connect_nodes_and_appear_in_find()
    {
        using var db = NewDb();
        var claude = db.Open("setting").Open("ai provider").Open("claude");
        var model = db.Open("models").Open("sonnet");
        model.Write("x");
        claude.Write("key");
        claude.Link("uses", model);

        Assert.Equal(["models.sonnet"], claude.Links("uses").Select(p => p.ToString()));
        Assert.Contains(claude.Find(), p => p.ToString() == "models.sonnet");

        model.Remove();
        Assert.Empty(claude.Links("uses"));
    }

    [Fact]
    public void Links_survive_reopening()
    {
        using (var db = NewDb())
        {
            db.Open("a").Write("1");
            db.Open("b").Write("2");
            db.Open("a").Link("next", db.Open("b"));
        }
        using var reopened = NewDb();
        Assert.Equal(["b"], reopened.Open("a").Links().Select(p => p.ToString()));
    }

    [Fact]
    public void Blobs_are_compressed_on_disk_and_round_trip()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compress me please ", 5000)));
        using (var db = NewDb())
        {
            db.Open("files").Open("log.txt").WriteBlob(data);
            Assert.Equal(data, db.Open("files").Open("log.txt").ReadBlob());
        }
        Assert.True(Directory.GetFiles(_dir, "seg-*.log").Sum(f => new FileInfo(f).Length) < data.Length / 10);
        using var reopened = NewDb();
        Assert.Equal(data, reopened.Open("files").Open("log.txt").ReadBlob());
        Assert.Throws<InvalidOperationException>(() => reopened.Open("files").Open("log.txt").Read());
    }

    [Fact]
    public void A_protected_value_is_ciphertext_on_disk_and_needs_the_key_to_read()
    {
        using (var db = NewDb())
        {
            db.Protect("$3cRet");
            db.Open("setting").Open("key").WriteProtect("TOP-SECRET-VALUE");
            Assert.Equal("TOP-SECRET-VALUE", db.Open("setting").Open("key").ReadProtect());
            Assert.Throws<InvalidOperationException>(() => db.Open("setting").Open("key").Read());
        }
        var raw = Directory.GetFiles(_dir, "seg-*.log").SelectMany(File.ReadAllBytes).ToArray();
        Assert.DoesNotContain("TOP-SECRET-VALUE", Encoding.Latin1.GetString(raw));

        using (var wrong = NewDb())
            Assert.Throws<UnauthorizedAccessException>(() => wrong.Protect("nope"));

        using var right = NewDb();
        right.Protect("$3cRet");
        Assert.Equal("TOP-SECRET-VALUE", right.Open("setting").Open("key").ReadProtect());
    }

    [Fact]
    public void Protected_ciphertext_cannot_be_moved_to_another_path()
    {
        using var db = NewDb();
        db.Protect("pw");
        db.Open("a").WriteProtect("secret");
        // Re-seal at "a" but replay the same ciphertext under "b" by copying raw bytes through the log is not
        // possible via the API, so check the cipher itself binds the path.
        var key = RandomNumberGenerator.GetBytes(32);
        var sealedValue = RecordCipher.Seal(key, "a", "secret"u8);
        Assert.Throws<AuthenticationTagMismatchException>(() => RecordCipher.Open(key, "b", sealedValue));
    }

    [Fact]
    public void Writing_protected_without_a_key_is_an_error()
    {
        using var db = NewDb();
        Assert.Throws<InvalidOperationException>(() => db.Open("a").WriteProtect("x"));
    }

    [Fact]
    public void A_pem_key_protects_and_reopens_the_database()
    {
        Directory.CreateDirectory(_dir);
        var pem = Path.Combine(_dir, "key.pem");
        File.WriteAllText(pem, RSA.Create(2048).ExportRSAPrivateKeyPem());
        var uri = new Uri(pem);

        using (var db = NewDb())
        {
            db.Protect(uri);
            db.Open("s").WriteProtect("v");
        }
        using var reopened = NewDb();
        reopened.Protect(uri);
        Assert.Equal("v", reopened.Open("s").ReadProtect());
    }

    [Fact]
    public void A_dpapi_key_protects_and_reopens_the_database()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var db = NewDb())
        {
            db.Protect(KeyProvider.Dpapi);
            db.Open("s").WriteProtect("v");
        }
        using var reopened = NewDb();
        reopened.Protect(KeyProvider.Dpapi);
        Assert.Equal("v", reopened.Open("s").ReadProtect());
        Assert.Throws<InvalidOperationException>(() => reopened.Protect("a passphrase"));
    }

    [Fact]
    public void Publish_reaches_subscribers_on_the_topic_and_above_but_not_after_unsubscribing()
    {
        using var db = NewDb();
        var topic = db.Open("channel").Open("topic");
        var onTopic = new List<string>();
        var onChannel = new List<string>();
        var sibling = new List<string>();
        using var a = topic.Subscribe(m => { lock (onTopic) onTopic.Add(m.Payload); });
        using var b = db.Open("channel").Subscribe(m => { lock (onChannel) onChannel.Add(m.Payload); });
        using var c = db.Open("channel").Open("other").Subscribe(m => { lock (sibling) sibling.Add(m.Payload); });

        Assert.Equal(2, topic.Publish("hello"));
        Eventually(() => { lock (onTopic) lock (onChannel) return onTopic.Count == 1 && onChannel.Count == 1; });
        Assert.Empty(sibling);

        a.Dispose();
        Assert.Equal(1, topic.Publish("again"));
        Eventually(() => { lock (onChannel) return onChannel.Count == 2; });
        Assert.Single(onTopic);
    }

    [Fact]
    public void Watch_reports_writes_and_removals_below_the_watched_node_only()
    {
        using var db = NewDb();
        var events = new List<DbEvent>();
        using var watch = db.Open("setting").Open("foo").Watch(e => { lock (events) events.Add(e); });

        db.Open("setting").Open("bar").Write("ignored");
        db.Open("setting").Open("foo").Open("x").Write("1");
        db.Open("setting").Open("foo").Open("x").Remove();

        Eventually(() => { lock (events) return events.Count == 2; });
        lock (events)
        {
            Assert.Equal(DbEventKind.Written, events[0].Kind);
            Assert.Equal("setting.foo.x", events[0].Path.ToString());
            Assert.Equal("1", events[0].Value);
            Assert.Equal(DbEventKind.Removed, events[1].Kind);
        }
    }

    [Fact]
    public void Watchers_never_see_protected_values()
    {
        using var db = NewDb();
        db.Protect("pw");
        var events = new List<DbEvent>();
        using var watch = db.Open("s").Watch(e => { lock (events) events.Add(e); });

        db.Open("s").Open("k").WriteProtect("secret");

        Eventually(() => { lock (events) return events.Count == 1; });
        lock (events) Assert.Null(events[0].Value);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_later_deliveries()
    {
        using var db = NewDb();
        var seen = 0;
        using var watch = db.Open("s").Watch(_ =>
        {
            if (Interlocked.Increment(ref seen) == 1) throw new InvalidOperationException("boom");
        });
        db.Open("s").Open("a").Write("1");
        db.Open("s").Open("b").Write("2");
        Eventually(() => Volatile.Read(ref seen) == 2);
    }

    [Fact]
    public void Names_cannot_be_empty_or_contain_the_separator()
    {
        using var db = NewDb();
        Assert.Throws<ArgumentException>(() => db.Open(""));
        Assert.Throws<ArgumentException>(() => db.Open("a\u001fb"));
    }

    [Fact]
    public void Stopwatch_throughput_smoke_test()
    {
        using var db = NewDb();
        var node = db.Open("bench");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++) node.Open("k" + i).Write("value-" + i);
        var writes = watch.Elapsed;
        watch.Restart();
        for (var i = 0; i < 20_000; i++) _ = node.Open("k" + i).Read();
        var reads = watch.Elapsed;
        Assert.True(writes < TimeSpan.FromSeconds(10) && reads < TimeSpan.FromSeconds(5), $"writes {writes}, reads {reads}");
    }

    private sealed class TaggedEmbedder(string model) : IEmbedder
    {
        public string Model => model;

        public float[] Embed(string text) => HashedTextEmbedderShim.Embed(text);
    }

    private static class HashedTextEmbedderShim
    {
        public static float[] Embed(string text) => Codale.Core.Features.HashedTextEmbedder.Instance.Embed(text);
    }
}
