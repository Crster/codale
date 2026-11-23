using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Codale.Db.Engine;

namespace Codale.Db;

public sealed partial class CodaleDatabase
{
    private sealed record IndexDefinition(string Name, string File, string KeyType, KeyKind Kind, string[] Members)
    {
        public IndexChange Project(string type, byte[] id, JsonElement value)
        {
            foreach (var member in Members)
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) break;
                if (value.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"Index '{Name}' selects a member from a non-object JSON value.");
                if (!value.TryGetProperty(member, out value)) break;
            }
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? new(File, SecondaryBucket(type), id)
                : new(File, SecondaryBucket(type), id, OrderedKey.Encode(Kind, value), JsonSerializer.SerializeToUtf8Bytes(value));
        }
    }

    private static string DefinitionBucket(string type) => "definitions:" + type;
    private static string SecondaryBucket(string type) => "secondary:" + type;

    private IndexDefinition[] Definitions(string type)
    {
        if (_definitions.TryGetValue(type, out var cached)) return cached;
        var definitions = _storage.Scan(FolderStorage.Records, DefinitionBucket(type))
            .Select(row => JsonSerializer.Deserialize<IndexDefinition>(row.Value)!).ToArray();
        _definitions[type] = definitions;
        return definitions;
    }

    internal void Build<T, TKey>(string name, Expression<Func<T, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var members = new Stack<string>();
        var expression = selector.Body;
        while (expression is MemberExpression { Expression: { } parent } member)
        {
            if (member.Member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                throw new ArgumentException("An index must select serialized members.", nameof(selector));
            var jsonName = member.Member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? Json.PropertyNamingPolicy!.ConvertName(member.Member.Name);
            members.Push(jsonName);
            expression = parent;
        }
        if (expression != selector.Parameters[0])
            throw new ArgumentException("An index selector must be a serialized member path or the record itself.", nameof(selector));

        var type = TypeName<T>();
        var definition = new IndexDefinition(name, FolderStorage.IndexFile(name), TypeName<TKey>(), OrderedKey.Kind(typeof(TKey)), members.ToArray());
        Access(() =>
        {
            var current = Definitions(type).FirstOrDefault(candidate => candidate.Name == name);
            if (current is not null && current.KeyType == definition.KeyType && current.Members.SequenceEqual(definition.Members))
                return true;
            var changes = new List<IndexChange> { new(definition.File, SecondaryBucket(type), null) };
            foreach (var row in _storage.Scan(FolderStorage.Records, RecordBucket(type)))
            {
                using var document = JsonDocument.Parse(_storage.ReadValue(row.Value));
                changes.Add(definition.Project(type, row.Id, document.RootElement));
            }
            changes.Add(new(FolderStorage.Records, DefinitionBucket(type), Id(name), Value: JsonSerializer.SerializeToUtf8Bytes(definition)));
            _definitions.Remove(type);
            _storage.Commit(changes);
            return true;
        });
    }

    internal IReadOnlyList<RecordIndexEntry<T, TKey>> FindIndex<T, TKey>(string name, Func<TKey, bool>? key, Func<T, bool>? filter,
        IndexBound<TKey>? lower = null, IndexBound<TKey>? upper = null, string? prefix = null)
    {
        var type = TypeName<T>();
        var rows = Access(() =>
        {
            var definition = Definitions(type).FirstOrDefault(candidate => candidate.Name == name)
                ?? throw new InvalidOperationException($"Index '{name}' has not been built for {typeof(T).Name}.");
            if (definition.KeyType != TypeName<TKey>())
                throw new InvalidOperationException($"Index '{name}' uses a different key type.");

            var start = prefix is null ? Encode(lower) : OrderedKey.Text(prefix);
            var end = prefix is null ? Encode(upper) : OrderedKey.AfterPrefix(start);
            var found = _storage.Scan(definition.File, SecondaryBucket(type), start, end, lower?.Inclusive ?? true, upper?.Inclusive ?? false);
            var snapshot = new List<(IndexRow Row, byte[] Address)>(found.Count);
            foreach (var row in found)
            {
                var address = Address(type, OrderedKey.ReadText(row.Id))
                    ?? throw new InvalidDataException($"Index '{name}' refers to a missing record.");
                snapshot.Add((row, address));
            }
            return snapshot;

            byte[]? Encode(IndexBound<TKey>? edge)
            {
                if (edge is not { } bound) return null;
                ArgumentNullException.ThrowIfNull(bound.Value);
                return OrderedKey.Encode(definition.Kind, JsonSerializer.SerializeToElement(bound.Value, Json));
            }
        });

        var matches = new List<RecordIndexEntry<T, TKey>>();
        foreach (var (row, address) in rows)
        {
            var indexKey = JsonSerializer.Deserialize<TKey>(row.Value, Json)!;
            if (key is not null && !key(indexKey)) continue;
            var value = Read<T>(address);
            if (filter is null || filter(value)) matches.Add(new(indexKey, OrderedKey.ReadText(row.Id), value));
        }
        return matches;
    }
}
