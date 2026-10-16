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
    private readonly HashSet<string> _summarizing = new(StringComparer.Ordinal);

    /// <summary>Summaries asked for while one was running, with whether any of those asks was forced.</summary>
    private readonly Dictionary<string, bool> _summaryAgain = new(StringComparer.Ordinal);

    public static FeatureActivity For(string projectPath) =>
        Projects.GetOrAdd(ProjectPaths.Normalize(projectPath), _ => new FeatureActivity());

    /// <summary>Held while a feature is looked up and created, so two chats naming the same topic end on one feature.</summary>
    public Lock CreateGate { get; } = new();

    /// <summary>Records where a chat is and what it is doing; replaces its earlier entry.</summary>
    public void SetChat(FeatureChat chat) => _chats[chat.ChatId] = chat;

    public void RemoveChat(string chatId) => _chats.TryRemove(chatId, out _);

    /// <summary>The chats on <paramref name="featureId"/> other than <paramref name="chatId"/>, busy ones first.</summary>
    public IReadOnlyList<FeatureChat> OthersOn(string featureId, string? chatId) =>
        _chats.Values
            .Where(c => c.FeatureId == featureId && c.ChatId != chatId)
            .OrderByDescending(c => c.IsBusy)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The chats on <paramref name="featureId"/> with a turn in flight.</summary>
    public IReadOnlySet<string> RunningOn(string featureId) =>
        _chats.Values.Where(c => c.FeatureId == featureId && c.IsBusy).Select(c => c.ChatId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Claims the right to summarise a feature. False when a summary is already being written: the
    /// ask is then remembered, and <see cref="EndSummary"/> hands it back so it runs once that one is done.
    /// </summary>
    public bool TryBeginSummary(string featureId, bool force)
    {
        lock (_summaryLock)
        {
            if (_summarizing.Add(featureId))
            {
                return true;
            }

            _summaryAgain[featureId] = force || _summaryAgain.GetValueOrDefault(featureId);
            return false;
        }
    }

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
