using Codale.Core.Agents;

namespace Codale.App.ViewModels;

/// <summary>
/// What the custom provider has served since the window opened: chat turns and background
/// requests alike. One per window, shared by every chat's status, so a new session does not
/// restart it; it ends only when the window closes.
/// </summary>
public sealed class CustomUsageTally
{
    public int Calls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    /// <summary>Raised on the thread that recorded; callers record on the UI thread.</summary>
    public event Action? Changed;

    public void Record(UsageSnapshot usage)
    {
        Calls++;
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadInputTokens;
        CacheWriteTokens += usage.CacheCreationInputTokens;
        Changed?.Invoke();
    }
}
