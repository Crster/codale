namespace Codale.Core.Text;

/// <summary>Where a piece of session text came from; it sets how much its words weigh.</summary>
public enum VocabularySource
{
    /// <summary>A prompt the user sent: their own words and topic.</summary>
    User,

    /// <summary>The agent's prose replies and narration.</summary>
    Assistant,

    /// <summary>Lines the agent added to files this session.</summary>
    Code,

    /// <summary>A file the agent read or edited this session.</summary>
    FileName,
}

/// <summary>
/// The words of one chat session - what the user wrote, what the agent answered, the names the agent
/// touched and the code it wrote - weighted by source and recency, plus which word followed which in
/// the prose. The composer completes, predicts and corrects from it, so suggestions follow the topic
/// of this conversation rather than the whole repository.
/// </summary>
/// <remarks>Not thread-safe: one owner feeds and queries it.</remarks>
public sealed class PromptVocabulary
{
    private const int MinWordLength = 3;
    private const int MaxWordLength = 64;

    private readonly Dictionary<string, Entry> _words = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, Dictionary<string, int>> _next = [with(StringComparer.Ordinal)];
    private int _seq;

    public int Count => _words.Count;

    public void Clear()
    {
        _words.Clear();
        _next.Clear();
        _seq = 0;
    }

    /// <summary>Learns the words of <paramref name="text"/>; later text counts as more recent.</summary>
    public void Add(string text, VocabularySource source)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var seq = ++_seq;
        if (source == VocabularySource.FileName)
        {
            AddFileName(text, seq);
            return;
        }

        var prose = source is VocabularySource.User or VocabularySource.Assistant;
        var weight = source switch
        {
            VocabularySource.User => 3.0,
            VocabularySource.Assistant => 1.0,
            _ => 1.5,
        };

        string? previous = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordStart(text[i]))
            {
                // Only a run of spaces keeps two words "next to each other"; punctuation or a line break ends the phrase.
                if (text[i] is not (' ' or '\t'))
                {
                    previous = null;
                }

                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && PromptWords.IsWordChar(text[i]))
            {
                i++;
            }

            var token = text[start..i];
            if (prose)
            {
                AddWord(NormalizeProse(token), weight, seq, source == VocabularySource.User);
                if (previous is not null && token.Length >= MinWordLength)
                {
                    AddFollower(previous, token, source == VocabularySource.User ? 2 : 1);
                }

                previous = token;
            }
            else if (token.Length > MinWordLength)
            {
                AddWord(token, weight, seq, fromUser: false);
                AddParts(token, weight * 0.4, seq);
            }

            // A dotted or pathed word ("ChatTab.xaml") is not a phrase with what follows.
            if (i < text.Length && text[i] is not (' ' or '\t'))
            {
                previous = null;
            }
        }
    }

    /// <summary>
    /// The best word starting with <paramref name="prefix"/> whose completion keeps exactly what was
    /// typed - "Comp" may become "Compile", but "sour" never turns into "SourceIndex" - or null.
    /// Words that followed <paramref name="previous"/> in this session and words already in
    /// <paramref name="draft"/> rank first.
    /// </summary>
    /// <param name="draft">The rest of the prompt being written (without the word being typed).</param>
    public string? Complete(string prefix, string? previous, string? draft = null) =>
        CompleteScored(prefix, previous, draft)?.Text;

    /// <summary>The score from which a session completion beats the general language's guess.</summary>
    public const double StrongScore = 2.0;

    /// <summary>
    /// Completes <paramref name="prefix"/> from the session and from <paramref name="general"/> - the
    /// system's language prediction for the same prefix, best first, already given the words before.
    /// A word this session is clearly about wins; then a general guess the session has used; then the
    /// general language's best; then whatever the session had. Every result keeps exactly what was typed.
    /// </summary>
    public string? Complete(string prefix, string? previous, string? draft, IReadOnlyList<string> general)
    {
        var session = CompleteScored(prefix, previous, draft);
        if (session is { Score: >= StrongScore } strong)
        {
            return strong.Text;
        }

        string? first = null;
        string? known = null;
        var knownScore = 0.0;
        foreach (var candidate in general)
        {
            if (prefix.Length < 2 || candidate.Length < prefix.Length + 2 ||
                !candidate.All(c => char.IsLetter(c) || c == '\'') ||
                !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = PromptWords.MatchCase(prefix, candidate);
            if (!result.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            first ??= result;
            var relevance = Relevance(candidate, previous);
            if (relevance > knownScore)
            {
                known = result;
                knownScore = relevance;
            }
        }

        return known ?? first ?? session?.Text;
    }

    /// <summary>
    /// <see cref="Complete"/> with how sure the session is: about 2 or more means a word the user wrote,
    /// a file or name the agent touched, or one that followed the previous word; below that, a word that
    /// only came up in passing.
    /// </summary>
    public (string Text, double Score)? CompleteScored(string prefix, string? previous, string? draft = null)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        var lower = prefix.ToLowerInvariant();
        var followers = previous is null ? null : _next.GetValueOrDefault(previous.ToLowerInvariant());

        // A single letter is too little to go on unless the phrase so far points at the word.
        if (prefix.Length < 2 && followers is null)
        {
            return null;
        }

        var inDraft = DraftWords(draft);
        string? best = null;
        var bestScore = 0.0;

        void Consider(string key, string spelled, double score)
        {
            // One letter left is not worth a keystroke.
            if (key.Length < prefix.Length + 2 || !key.StartsWith(lower, StringComparison.Ordinal))
            {
                return;
            }

            var result = PromptWords.MatchCase(prefix, spelled);
            if (!result.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }

            var follows = followers?.GetValueOrDefault(key) ?? 0;
            if (prefix.Length < 2 && follows == 0)
            {
                return;
            }

            if (follows > 0)
            {
                score += 2 + 1.5 * Math.Log2(1 + follows);
            }

            if (inDraft.Contains(key))
            {
                score += 1.5;
            }

            if (score > bestScore || (score == bestScore && best is not null && result.Length < best.Length))
            {
                best = result;
                bestScore = score;
            }
        }

        foreach (var (key, entry) in _words)
        {
            Consider(key, entry.Spelling, Rank(entry));
        }

        // Words only the current draft has used so far.
        if (draft is not null)
        {
            foreach (var token in Tokens(draft))
            {
                var key = token.ToLowerInvariant();
                if (token.Length >= MinWordLength && !_words.ContainsKey(key))
                {
                    Consider(key, NormalizeProse(token), 1.0);
                }
            }
        }

        return best is null ? null : (best, bestScore);
    }

    /// <summary>
    /// The word that usually comes after <paramref name="previous"/> in this session, when one clearly
    /// does: seen at least twice after it and at least half the time. Null otherwise.
    /// </summary>
    public string? NextWord(string previous)
    {
        if (!_next.TryGetValue(previous.ToLowerInvariant(), out var followers))
        {
            return null;
        }

        var total = 0;
        string? best = null;
        var bestCount = 0;
        foreach (var (word, count) in followers)
        {
            total += count;
            if (count > bestCount)
            {
                best = word;
                bestCount = count;
            }
        }

        return best is null || bestCount < 2 || bestCount * 2 < total || !_words.TryGetValue(best, out var entry) ? null : entry.Spelling;
    }

    /// <summary>
    /// True when the session vouches for the spelling: the agent or the code used it, or the user typed
    /// it more than once (a one-off typo of theirs does not count).
    /// </summary>
    public bool Knows(string word) =>
        _words.TryGetValue(word.ToLowerInvariant(), out var entry) && (entry.Trusted > 0 || entry.UserCount >= 2);

    /// <summary>How strongly this session is about <paramref name="word"/>; 0 when it never came up.</summary>
    public double Relevance(string word, string? previous = null)
    {
        var key = word.ToLowerInvariant();
        if (!_words.TryGetValue(key, out var entry))
        {
            return 0;
        }

        var score = Rank(entry);
        if (previous is not null && _next.TryGetValue(previous.ToLowerInvariant(), out var followers) &&
            followers.TryGetValue(key, out var count))
        {
            score += 2 + 1.5 * Math.Log2(1 + count);
        }

        return score;
    }

    /// <summary>Trusted session words spelled within <paramref name="maxEdits"/> of <paramref name="word"/>: fewest edits, then most relevant.</summary>
    public IReadOnlyList<(string Text, int Distance)> Near(string word, int maxEdits, string? previous = null, int max = 3)
    {
        if (word.Length < MinWordLength)
        {
            return [];
        }

        var lower = word.ToLowerInvariant();
        var limit = Math.Min(maxEdits, word.Length <= 4 ? 1 : 2);
        var found = new List<(string Text, int Distance, double Score)>();
        foreach (var (key, entry) in _words)
        {
            if (Math.Abs(key.Length - lower.Length) > limit || (entry.Trusted <= 0 && entry.UserCount < 2))
            {
                continue;
            }

            var distance = EditDistance(lower, key, limit);
            if (distance > 0 && distance <= limit)
            {
                found.Add((entry.Spelling, distance, Relevance(key, previous)));
            }
        }

        return [.. found
            .OrderBy(f => f.Distance)
            .ThenByDescending(f => f.Score)
            .Take(max)
            .Select(f => (f.Text, f.Distance))];
    }

    /// <summary>Edits between two words (a swap of neighbours counts once), or <paramref name="limit"/> + 1 once it must exceed it.</summary>
    public static int EditDistance(string a, string b, int limit)
    {
        var n = a.Length;
        var m = b.Length;
        if (Math.Abs(n - m) > limit)
        {
            return limit + 1;
        }

        var before = new int[m + 1];
        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            var best = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, before[j - 2] + 1);
                }

                current[j] = value;
                best = Math.Min(best, value);
            }

            if (best > limit)
            {
                return limit + 1;
            }

            (before, previous, current) = (previous, current, before);
        }

        return previous[m];
    }

    private double Rank(Entry entry)
    {
        var recency = _seq == 0 ? 1 : 0.35 + 0.65 * entry.LastSeen / _seq;
        return Math.Log2(1 + entry.Weight) * recency;
    }

    private void AddFileName(string path, int seq)
    {
        var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        var dot = name.IndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        foreach (var token in Tokens(stem))
        {
            if (token.Length >= MinWordLength)
            {
                // "composer" from ComposerPredictor.cs is nearly as much the topic as the name itself.
                AddWord(token, 4, seq, fromUser: false);
                AddParts(token, 3, seq);
            }
        }
    }

    /// <summary>The camel-case and snake-case parts of a name, as plain lower-case words ("ComposerPredictor" gives "composer", "predictor").</summary>
    private void AddParts(string token, double weight, int seq)
    {
        var start = 0;
        for (var i = 1; i <= token.Length; i++)
        {
            var boundary = i == token.Length || token[i] == '_' ||
                           (char.IsUpper(token[i]) && (char.IsLower(token[i - 1]) || (i + 1 < token.Length && char.IsLower(token[i + 1]))));
            if (!boundary)
            {
                continue;
            }

            var part = token[start..i].Trim('_');
            if (part.Length >= 4 && part.Length < token.Length && part.All(char.IsLetter))
            {
                AddWord(part.ToLowerInvariant(), weight, seq, fromUser: false);
            }

            start = i;
        }
    }

    private void AddWord(string spelled, double weight, int seq, bool fromUser)
    {
        if (spelled.Length < MinWordLength || spelled.Length > MaxWordLength)
        {
            return;
        }

        var key = spelled.ToLowerInvariant();
        if (!_words.TryGetValue(key, out var entry))
        {
            _words[key] = entry = new Entry();
        }

        entry.Weight += weight;
        entry.LastSeen = seq;
        if (fromUser)
        {
            entry.UserCount++;
        }
        else
        {
            entry.Trusted += weight;
        }

        entry.Spellings[spelled] = entry.Spellings.GetValueOrDefault(spelled) + 1;
        entry.Spelling = Preferred(entry.Spellings);
    }

    private void AddFollower(string previous, string word, int count)
    {
        var key = previous.ToLowerInvariant();
        if (!_next.TryGetValue(key, out var followers))
        {
            _next[key] = followers = [with(StringComparer.Ordinal)];
        }

        var next = word.ToLowerInvariant();
        followers[next] = followers.GetValueOrDefault(next) + count;
    }

    /// <summary>Lower case when the word was seen in lower case often enough; otherwise the most common spelling (a name).</summary>
    private static string Preferred(Dictionary<string, int> spellings)
    {
        var best = "";
        var bestCount = 0;
        var lowerCount = 0;
        string? lower = null;
        foreach (var (spelled, count) in spellings)
        {
            if (count > bestCount)
            {
                best = spelled;
                bestCount = count;
            }

            if (!spelled.Any(char.IsUpper))
            {
                lower = spelled;
                lowerCount = count;
            }
        }

        return lower is not null && lowerCount * 3 >= bestCount ? lower : best;
    }

    /// <summary>
    /// A prose word written "Like" this is most likely just sentence case: it is kept in lower case so
    /// completing it mid-sentence does not capitalise it. Names with inner capitals ("ChatTab") keep theirs.
    /// </summary>
    private static string NormalizeProse(string token) =>
        token.Length > 1 && char.IsUpper(token[0]) && token.Skip(1).All(c => char.IsLower(c))
            ? token.ToLowerInvariant()
            : token;

    private static HashSet<string> DraftWords(string? draft)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (draft is not null)
        {
            foreach (var token in Tokens(draft))
            {
                set.Add(token.ToLowerInvariant());
            }
        }

        return set;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordStart(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && PromptWords.IsWordChar(text[i]))
            {
                i++;
            }

            yield return text[start..i];
        }
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private sealed class Entry
    {
        public Dictionary<string, int> Spellings { get; } = [with(StringComparer.Ordinal)];
        public string Spelling { get; set; } = "";
        public double Weight { get; set; }
        public double Trusted { get; set; }
        public int UserCount { get; set; }
        public int LastSeen { get; set; }
    }
}