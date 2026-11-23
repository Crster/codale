using System.Text.Json;

using Codale.Db.Engine;

namespace Codale.Db;

public sealed partial class CodaleDatabase
{
    /// <summary>Stages typed values, deletions and search entries, then commits them together.</summary>
    public void WriteBatch(Action<RecordBatch> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        var batch = new RecordBatch();
        prepare(batch);
        var items = batch.Items.Values.ToArray();
        Access(() =>
        {
            var changes = new List<IndexChange>();
            foreach (var item in items)
            {
                if (item.Value is { } value)
                {
                    StageValue(item.Type, item.Key, value, Definitions(item.Type), changes);
                    if (item.Search is { } search) StageSemantic(search, new(item.Type, item.Key), changes);
                }
                else if (Address(item.Type, item.Key) is not null) StageDelete(item.Type, item.Key, changes);
            }
            CommitRecords(changes);
            return true;
        });
    }

    public sealed class RecordBatch
    {
        internal sealed record Item(string Type, string Key, byte[]? Value, PreparedSearch? Search);
        internal Dictionary<(string Type, string Key), Item> Items { get; } = [];
        internal RecordBatch() { }

        public void Put<T>(string key, T value, SemanticKey? search = null)
        {
            var type = TypeName<T>();
            ValidateKey(key);
            Items[(type, key)] = new(type, key, JsonSerializer.SerializeToUtf8Bytes(value, Json), search?.Prepare());
        }

        public void Delete<T>(string key)
        {
            var type = TypeName<T>();
            ValidateKey(key);
            Items[(type, key)] = new(type, key, null, null);
        }
    }
}
