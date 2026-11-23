using Codale.Db.Engine;
using Codale.Db.Events;

namespace Codale.Db;

public enum RecordAction { Inserted, Updated, Deleted }

public sealed partial class CodaleDatabase
{
    private readonly record struct Change(string Type, string Key, ReadOnlyMemory<byte> Value, RecordAction Action);

    private readonly Hub<Change> _watches = new((subscribed, type, _) => subscribed == type, unbounded: true);

    internal IDisposable Watch<T>(Action<RecordEntry<T>, RecordAction> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Access(() => _watches.Subscribe(TypeName<T>(), change =>
            handler(new(change.Key, DeserializeRecord<T>(change.Value)!), change.Action)));
    }

    private IReadOnlyList<Change> CaptureChanges(List<IndexChange> changes)
    {
        if (_watches.IsEmpty) return [];
        var final = new Dictionary<(string Type, string Key), byte[]?>();
        foreach (var change in changes)
            if (RecordIdentity(change) is { } identity && _watches.HasSubscriber(identity.Type))
                final[identity] = change.Value;

        var notifications = new List<Change>(final.Count);
        foreach (var (identity, address) in final)
        {
            var previous = Address(identity.Type, identity.Key);
            if ((address ?? previous) is not { } payload) continue;
            var action = address is null ? RecordAction.Deleted : previous is null ? RecordAction.Inserted : RecordAction.Updated;
            notifications.Add(new(identity.Type, identity.Key, _storage.ReadValue(payload), action));
        }
        return notifications;
    }
}
