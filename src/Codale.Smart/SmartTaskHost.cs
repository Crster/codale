namespace Codale.Smart;

/// <summary>
/// The narrow surface the Smart task helpers need from the running host: whether a task's feature is switched on,
/// whether the model is ready, and one generation. The application's host implements it; tests use a fake.
/// </summary>
public interface ISmartTaskHost
{
    /// <summary>Whether the feature that serves <paramref name="task"/> is switched on in the settings.</summary>
    bool IsFeatureEnabled(string task);

    /// <summary>Whether the local runtime has loaded and can answer.</summary>
    bool IsReady { get; }

    /// <summary>One generation for a task; null when the model gives no usable answer.</summary>
    Task<SmartResult<T>?> GenerateAsync<T>(SmartRequestContext context, string system, string input, int cap,
        Func<string, T?> parse, bool background, CancellationToken ct) where T : class;
}