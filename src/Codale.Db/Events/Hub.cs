using System.Threading.Channels;

namespace Codale.Db;

/// <summary>
/// Fan-out to subscribers. Every subscriber owns a bounded queue and a pump task, so a slow or throwing
/// handler never blocks a writer or another subscriber (the oldest queued item is dropped on overflow).
/// </summary>
internal sealed class Hub<T> : IDisposable
{
    private const int QueueCapacity = 1024;

    private readonly Func<string, string, T, bool> _matches;
    private readonly object _gate = new();
    private Subscription[] _subscriptions = [];

    /// <param name="matches">(subscribedPath, itemPath, item) → whether the subscriber should see the item.</param>
    public Hub(Func<string, string, T, bool> matches) => _matches = matches;

    public bool IsEmpty => _subscriptions.Length == 0;

    public IDisposable Subscribe(string path, Action<T> handler)
    {
        var subscription = new Subscription(this, path, handler);
        lock (_gate) _subscriptions = [.. _subscriptions, subscription];
        return subscription;
    }

    /// <summary>Queues the item for every matching subscriber and returns how many there were.</summary>
    public int Publish(string path, T item)
    {
        var count = 0;
        foreach (var subscription in _subscriptions)
        {
            if (!_matches(subscription.Path, path, item)) continue;
            subscription.Queue.Writer.TryWrite(item);
            count++;
        }
        return count;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate) _subscriptions = _subscriptions.Where(s => s != subscription).ToArray();
    }

    public void Dispose()
    {
        Subscription[] all;
        lock (_gate)
        {
            all = _subscriptions;
            _subscriptions = [];
        }
        foreach (var subscription in all) subscription.Queue.Writer.TryComplete();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Hub<T> _hub;

        public Subscription(Hub<T> hub, string path, Action<T> handler)
        {
            _hub = hub;
            Path = path;
            Queue = Channel.CreateBounded<T>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            _ = Task.Run(async () =>
            {
                await foreach (var item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try { handler(item); }
                    catch { /* a subscriber's bug must not stop its own later deliveries */ }
                }
            });
        }

        public string Path { get; }
        public Channel<T> Queue { get; }

        public void Dispose()
        {
            _hub.Remove(this);
            Queue.Writer.TryComplete();
        }
    }
}
