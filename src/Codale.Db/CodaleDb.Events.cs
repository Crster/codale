namespace Codale.Db;

/// <summary>Change events (<c>Watch</c>) and pub/sub (<c>Publish</c>/<c>Subscribe</c>). Both deliver off the writer's thread.</summary>
public sealed partial class CodaleDb
{
    private readonly Hub<DbEvent> _events = new((subscribed, path, e) => e.Kind == DbEventKind.Removed
        ? IsUnder(path, subscribed) || IsUnder(subscribed, path)
        : IsUnder(path, subscribed));

    private readonly Hub<DbMessage> _messages = new((subscribed, path, _) => IsUnder(path, subscribed));

    internal IDisposable Watch(string path, Action<DbEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _events.Subscribe(path, handler);
    }

    internal int Publish(string path, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _messages.IsEmpty ? 0 : _messages.Publish(path, new DbMessage(DbPath.FromRaw(path), payload));
    }

    internal IDisposable Subscribe(string path, Action<DbMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _messages.Subscribe(path, handler);
    }

    private void Emit(string path, DbEventKind kind, string? value)
    {
        if (!_events.IsEmpty) _events.Publish(path, new DbEvent(DbPath.FromRaw(path), kind, value));
    }

    private static bool IsUnder(string path, string ancestor) =>
        ancestor.Length == 0
        || path.Equals(ancestor, StringComparison.Ordinal)
        || (path.Length > ancestor.Length && path[ancestor.Length] == Sep && path.StartsWith(ancestor, StringComparison.Ordinal));
}
