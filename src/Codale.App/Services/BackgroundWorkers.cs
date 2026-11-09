namespace Codale.App.Services;

/// <summary>
/// How many background jobs (commit messages, titles, routing, search helpers and ghost text) are
/// running right now. A job holds a <see cref="Track"/> scope for as long as it is in flight.
/// </summary>
public static class BackgroundWorkers
{
    private static int _running;

    /// <summary>Jobs in flight across the app.</summary>
    public static int Running => Volatile.Read(ref _running);

    /// <summary>Raised when a job starts or ends. May be raised on any thread.</summary>
    public static event Action? Changed;

    /// <summary>Counts one job until the returned scope is disposed.</summary>
    public static IDisposable Track()
    {
        Interlocked.Increment(ref _running);
        Changed?.Invoke();
        return new Job();
    }

    private sealed class Job : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref _running);
                Changed?.Invoke();
            }
        }
    }
}
