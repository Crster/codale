using System.Text.Json;
using System.Threading.Channels;

using Codale.Db.Engine;
using Codale.Db.Search;

using Microsoft.Data.Sqlite;

namespace Codale.Db.Tests;

public sealed class CodaleDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codale-folder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Typed_records_and_named_indexes_survive_reopen_and_track_updates()
    {
        using (var db = new CodaleDatabase(_directory))
        {
            var users = db.Record<User>();
            users.Key("mc-003").Value(new("Zoe", -6));
            users.Key("mc-001").Value(new("McDoe", 50));
            users.Key("mc-002").Value(new("McDoe", 10));
            db.Record<Product>().Key("mc-001").Value(new("Water"));
            var names = users.Index("by-name").Build(user => user.Name);
            var ages = users.Index("by-age").Build(user => user.Age);

            Assert.Equal(["mc-001", "mc-002", "mc-003"], users.FindPrefix("mc").Select(row => row.Key));
            Assert.Equal(["mc-001", "mc-002"], names.FindPrefix("Mc").Select(row => row.Key));
            Assert.Equal([-6, 10, 50], ages.Find().Select(row => row.IndexKey));
            Assert.Equal([10], ages.FindRange(new(0, false), new(50, false)).Select(row => row.IndexKey));
            Assert.Equal(["mc-001"], ages.Find(age => age > 5, user => user.Age == 50).Select(row => row.Key));
            Assert.Throws<IOException>(() => new CodaleDatabase(_directory));
        }

        using var reopened = new CodaleDatabase(_directory);
        var stored = reopened.Record<User>();
        Assert.Equal(new User("McDoe", 50), stored.Key("mc-001").Get());
        Assert.Equal(new Product("Water"), reopened.Record<Product>().Key("mc-001").Get());
        Assert.True(stored.Key("mc-001").Set(new Dictionary<string, object?> { ["AGE"] = 5, ["name"] = "McRae" }));
        Assert.False(stored.Key("missing").Set(new { Age = 7 }));
        Assert.Throws<JsonException>(() => stored.Key("mc-001").Set(new { Age = "invalid" }));
        Assert.Equal(new User("McRae", 5), stored.Key("mc-001").Get());
        Assert.Equal(["McDoe", "McRae"], stored.Index<string>("by-name").FindPrefix("Mc").Select(row => row.IndexKey));
        Assert.Single(stored.Index<int>("by-age").FindRange(new(5), new(5)));
        var shared = stored.Key("mc-001");
        Parallel.Invoke(() => shared.Set(new { Name = "Both" }), () => shared.Set(new { Age = 21 }));
        Assert.Equal(new User("Both", 21), shared.Get());
        Assert.Equal(new User("Both", 21), stored.Key("mc-001").Delete());
        Assert.False(stored.Key("mc-001").TryGet(out _));
        Assert.Empty(stored.Index<int>("by-age").FindRange(new(21), new(21)));
        Assert.True(File.Exists(Path.Combine(_directory, "LOCK")));
        Assert.True(File.Exists(Path.Combine(_directory, "tmp.db")));
        Assert.True(File.Exists(Path.Combine(_directory, "index_by-name.db")));
        Assert.True(File.Exists(Path.Combine(_directory, "index_by-age.db")));
        Assert.Single(Directory.GetFiles(_directory, "record_*.db"));
    }

    [Fact]
    public void Semantic_and_relationship_indexes_resolve_and_remove_typed_references()
    {
        using (var db = new CodaleDatabase(_directory, new() { Embedder = new WaterEmbedder() }))
        {
            var water = db.Open<Product>().Key("product-001");
            var juice = db.Open<Product>().Key("product-002");
            var mcd = db.Open<User>().Key("user-001");
            var other = db.Open<User>().Key("user-002");
            water.Value(new("Water"));
            juice.Value(new("Juice"));
            mcd.Value(new("McDoe", 50));
            other.Value(new("Smith", 20));
            var products = db.Index("product");
            products.Key(["gallon of water"]).Value(water);
            products.Key(["juice"], [0, 1]).Value(juice);
            db.Index("lexical").Key(["citrus juice"], mode: SemanticMode.Bm25).Value(juice);
            Assert.Equal(2, db.Link("buys").Keys(water, mcd, juice));
            Assert.True(db.Link("buys").Key(other, juice));
            Assert.False(db.Link("buys").Key(juice, other));
        }

        using var reopened = new CodaleDatabase(_directory, new() { Embedder = new WaterEmbedder() });
        var product = reopened.Open<Product>().Key("product-002");
        var user = reopened.Open<User>().Key("user-002");
        var productsIndex = reopened.Index("product");
        var hit = productsIndex.Find("a galloon of water", 1).Single();
        Assert.Equal("product-001", hit.Key);
        Assert.Equal(new Product("Water"), hit.Record<Product>()!.Get());
        Assert.Null(hit.Record<User>());
        Assert.Equal(product.Key, productsIndex.Find([0f, 2f], 1).Single().Key);
        Assert.Equal(product.Key, reopened.Index("lexical").Find("citrus").Single().Key);

        var links = reopened.Using(product).Match(LinkFilter.Where<User>("buys", user => user.Name.StartsWith("Mc", StringComparison.Ordinal)));
        Assert.Equal("user-001", Assert.Single(links).Key);
        Assert.True(links[0].TryGet<User>(out var linkedUser));
        Assert.Equal("McDoe", linkedUser!.Name);
        Assert.True(reopened.Unlink("buys").Key(user, product));
        Assert.Empty(reopened.Using(user).Match());

        Assert.Equal(new Product("Juice"), product.Delete());
        product.Value(new("A different product"));
        Assert.Empty(reopened.Using(product).Match());
        Assert.DoesNotContain(reopened.Using(reopened.Open<User>().Key("user-001")).Match(), row => row.Key == product.Key);
        Assert.Empty(reopened.Index("lexical").Find("citrus"));
        Assert.Empty(productsIndex.Find([0f, 1f]));
    }

    [Fact]
    public async Task Watch_delivers_committed_typed_snapshots_and_the_deleted_value()
    {
        using var db = new CodaleDatabase(_directory, new() { RecordBlockBytes = 256 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var changes = Channel.CreateUnbounded<(RecordEntry<User> Record, RecordAction Action)>();
        using var watch = db.Open<User>().Watch((record, action) => changes.Writer.TryWrite((record, action)));
        var user = db.Open<User>().Key("one");

        Assert.Throws<ArgumentException>(() => db.WriteBatch(batch =>
        {
            batch.Put("discarded", new User("small", 1));
            batch.Put("large", new User(new string('x', 300), 2));
        }));
        db.Open<Product>().Key("other").Value(new("Water"));
        user.Value(new("McDoe", 20));
        Assert.True(user.Set(new { Age = 21 }));
        db.WriteBatch(batch => batch.Put("one", new User("McRae", 22)));
        Assert.Equal(new User("McRae", 22), user.Delete());
        Assert.Null(user.Delete());

        Assert.Equal((new RecordEntry<User>("one", new("McDoe", 20)), RecordAction.Inserted), await changes.Reader.ReadAsync(timeout.Token));
        Assert.Equal((new RecordEntry<User>("one", new("McDoe", 21)), RecordAction.Updated), await changes.Reader.ReadAsync(timeout.Token));
        Assert.Equal((new RecordEntry<User>("one", new("McRae", 22)), RecordAction.Updated), await changes.Reader.ReadAsync(timeout.Token));
        Assert.Equal((new RecordEntry<User>("one", new("McRae", 22)), RecordAction.Deleted), await changes.Reader.ReadAsync(timeout.Token));
        Assert.False(changes.Reader.TryRead(out _));

        var zero = db.Open<int>().Key("zero");
        zero.Value(0);
        Assert.True(zero.TryDelete(out var removed));
        Assert.Equal(0, removed);
        Assert.False(zero.TryDelete(out _));
    }

    [Fact]
    public void Concurrent_writers_and_indexed_reads_are_consistent_while_record_blocks_roll()
    {
        using (var db = new CodaleDatabase(_directory, new() { RecordBlockBytes = 256 }))
        {
            var records = db.Record<User>();
            var ages = records.Index("by-age").Build(user => user.Age);
            Parallel.For(0, 40, i =>
            {
                var key = $"k-{i:D2}";
                records.Key(key).Value(new(new string('a', 60), i));
                Assert.Equal(i, records.Key(key).Get()!.Age);
                Assert.Equal(i, ages.FindRange(new(i), new(i)).Single().Value.Age);
            });
            Assert.Throws<ArgumentException>(() => records.PutMany([
                new("k-00", new("replacement", -1)), new("too-big", new(new string('x', 256), 9)),
            ]));
            Assert.Equal(0, records.Key("k-00").Get()!.Age);
            Assert.Empty(ages.FindRange(new(-1), new(-1)));
        }

        var blocks = Directory.GetFiles(_directory, "record_*.db");
        Assert.True(blocks.Length > 1);
        Assert.All(blocks, path => Assert.InRange(new FileInfo(path).Length, 0, 256));
        using var reopened = new CodaleDatabase(_directory);
        Assert.Equal(Enumerable.Range(0, 40), reopened.Record<User>().Index<int>("by-age").Find().Select(row => row.IndexKey));
    }

    [Fact]
    public void A_queued_write_finishes_on_open_when_only_one_index_was_updated()
    {
        const string records = "test-records";
        const string ages = "test-ages";
        var id = OrderedKey.Text("one");
        var path = Path.Combine(_directory, "tmp.db");
        using (var storage = new FolderStorage(_directory, 4096, TimeSpan.Zero))
        {
            var address = storage.AppendValue("new value"u8);
            IndexChange[] changes = [new(FolderStorage.Records, records, id, Value: address), new("by-age", ages, id, Value: [11])];
            storage.Commit([changes[0]]);
            using var queue = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            queue.Open();
            using var command = queue.CreateCommand();
            command.CommandText = "INSERT INTO pending (id, changes) VALUES (1, $changes)";
            command.Parameters.AddWithValue("$changes", JsonSerializer.SerializeToUtf8Bytes(changes));
            command.ExecuteNonQuery();
            Assert.Null(storage.Lookup("by-age", ages, id));
        }

        using (var reopened = new FolderStorage(_directory, 4096, TimeSpan.Zero))
        {
            Assert.Equal("new value"u8.ToArray(), reopened.ReadValue(reopened.Lookup(FolderStorage.Records, records, id)!).ToArray());
            Assert.Equal(new byte[] { 11 }, reopened.Lookup("by-age", ages, id));
        }
        using var queueCheck = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        queueCheck.Open();
        using var pending = queueCheck.CreateCommand();
        pending.CommandText = "SELECT count(*) FROM pending";
        Assert.Equal(0L, pending.ExecuteScalar());
    }

    private sealed record User(string Name, int Age);
    private sealed record Product(string Name);

    private sealed class WaterEmbedder : IEmbedder
    {
        public string Model => "test-water";
        public float[] Embed(string text) => text.Contains("water", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f];
    }
}
