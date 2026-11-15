namespace Codale.Core.Agents;

/// <summary>Where a Smart request reports its progress. The application's status tracker implements it.</summary>
public interface ISmartRequestStatus
{
    void Start(string id, string task);
    void Finish(string id, string outcome, string? error = null);
}

/// <summary>
/// The two request shapes the Smart helpers share: a request tracked on the Smart status (completed, cancelled or
/// error), and a helper request with a deadline that gives no answer when it runs out.
/// </summary>
public static class SmartRequestWorkflow
{
    /// <summary>
    /// Runs <paramref name="work"/> with its progress on the status. A cancelled request is reported and rethrown; a
    /// failed one is reported with its message and gives null, so the caller falls back.
    /// </summary>
    public static async Task<T?> RunTrackedAsync<T>(ISmartRequestStatus status, string task,
        Func<CancellationToken, Task<T?>> work, CancellationToken ct) where T : class
    {
        var id = Guid.NewGuid().ToString("N");
        status.Start(id, task);
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            status.Finish(id, "completed");
            return result;
        }
        catch (OperationCanceledException)
        {
            status.Finish(id, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            status.Finish(id, "error", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a deadline. When the deadline passes and the caller did not cancel, the answer
    /// is null rather than an error; a caller's own cancellation still propagates.
    /// </summary>
    public static async Task<T?> RunWithDeadlineAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T?>> work,
        CancellationToken ct) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await work(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
