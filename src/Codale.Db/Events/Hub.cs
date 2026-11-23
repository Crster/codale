using System.Threading.Channels;

namespace Codale.Db.Events;

/// <summary>
/// Fan-out to subscribers. Each subscriber owns a queue and a pump task, so its handler never blocks a writer
/// or another subscriber. Bounded subscriptions drop their oldest queued item on overflow.
/// </summary>
/// <param name="matches">(subscribedPath, itemPath, item) → whether the subscriber should see the item.</param>
internal sealed class Hub<T>(Func<string, string, T, bool> matches, bool unbounded = false) : IDisposable
{
    private const int QueueCapacity = 1024;

    private readonly Func<string, string, T, bool> _matches = matches;
    private readonly Lock _gate = new();
    private volatile Subscription[] _subscriptions = [];

    public bool IsEmpty => _subscriptions.Length == 0;
    public bool HasSubscriber(string path) => _subscriptions.Any(subscription => subscription.Path == path);

    public IDisposable Subscribe(string path, Action<T> handler)
    {
        var subscription = new Subscription(this, path, handler, unbounded);
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
        lock (_gate) _subscriptions = [.. _subscriptions.Where(s => s != subscription)];
    }

    public void Dispose()
    {
        Subscription[] all;
        lock (_gate)
        {
            all = _subscriptions;
            _subscriptions = [];
        }
        foreach (var subscription in all) subscription.Stop();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Hub<T> _hub;
        private volatile bool _stopped;

        public Subscription(Hub<T> hub, string path, Action<T> handler, bool unbounded)
        {
            _hub = hub;
            Path = path;
            Queue = unbounded ? Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true })
                : Channel.CreateBounded<T>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            _ = Task.Run(async () =>
            {
                await foreach (var item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (_stopped) break;
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
            Stop();
        }

        public void Stop()
        {
            _stopped = true;
            Queue.Writer.TryComplete();
        }
    }
}
