using System.Collections.Concurrent;

using Codale.Core.Features;
using Codale.Core.Projects;

namespace Codale.App.Services;

/// <summary>
/// What every window open on one project shares about its features, beyond the database and logs
/// on disk: which chats are on which feature right now, which summaries are being written, and the
/// gate that keeps two chats from creating the same feature at once. Each window has its own
/// <see cref="FeatureService"/>; they all reach the same instance of this through <see cref="For"/>.
/// </summary>
internal sealed class FeatureActivity
{
    private static readonly ConcurrentDictionary<string, FeatureActivity> Projects = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, FeatureChat> _chats = new(StringComparer.Ordinal);
    private readonly Lock _summaryLock = new();
    private readonly HashSet<string> _summarizing = [with(StringComparer.Ordinal)];

    /// <summary>Notifies every window on the project so its open chats detach from deleted history.</summary>
    public event EventHandler? HistoryDeleted;

    public void NotifyHistoryDeleted(string featureId)
    {
        foreach (var chat in _chats.Values.Where(c => c.FeatureId == featureId))
        {
            _chats.TryRemove(chat.ChatId, out _);
        }

        HistoryDeleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Summaries asked for while one was running, with whether any of those asks was forced.</summary>
    private readonly Dictionary<string, bool> _summaryAgain = [with(StringComparer.Ordinal)];

    public static FeatureActivity For(string projectPath) =>
        Projects.GetOrAdd(ProjectPaths.RepositoryIdentity(projectPath), _ => new FeatureActivity());

    /// <summary>Records where a chat is and what it is doing; replaces its earlier entry.</summary>
    public void SetChat(FeatureChat chat) => _chats[chat.ChatId] = chat;

    public void RemoveChat(string chatId) => _chats.TryRemove(chatId, out _);

    /// <summary>The chats on <paramref name="featureId"/> other than <paramref name="chatId"/>, busy ones first.</summary>
    public IReadOnlyList<FeatureChat> OthersOn(string featureId, string? chatId) =>
        [.. _chats.Values
            .Where(c => c.FeatureId == featureId && c.ChatId != chatId)
            .OrderByDescending(c => c.IsBusy)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Releases the feature; returns whether a summary was asked for meanwhile, and whether forced.</summary>
    public (bool Again, bool Force) EndSummary(string featureId)
    {
        lock (_summaryLock)
        {
            _summarizing.Remove(featureId);
            return _summaryAgain.Remove(featureId, out var force) ? (true, force) : (false, false);
        }
    }
}