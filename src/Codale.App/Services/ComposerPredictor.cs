using Codale.Core.Text;
using Codale.Search;

namespace Codale.App.Services;

/// <summary>What the composer offers at the caret: replace a span of the prompt with <see cref="Replacement"/>.</summary>
/// <param name="IsCorrection">A spelling fix for a finished word, rather than the rest of the word being typed.</param>
internal sealed record ComposerPrediction(int Start, int Length, string Replacement, bool IsCorrection)
{
    public int End => Start + Length;
}

/// <summary>
/// Predicts from the workspace's own vocabulary - every identifier, declared name and comment word
/// the search index has learned, ranked by what followed the previous word in the code - so the
/// prompt names things that really exist. A word neither the workspace nor the Windows dictionary
/// knows gets the closest spelling, the workspace's first.
/// </summary>
/// <remarks>
/// <see cref="Predict"/> may take a few milliseconds (the first call loads the model), so it runs off
/// the UI thread through <see cref="Request"/>, which keeps one call in flight and drops requests
/// the user has already typed past.
/// </remarks>
internal sealed class ComposerPredictor(string projectRoot)
{
    private const int MinCorrectionLength = 3;
    private const int MinCompletionPrefix = 2;
    private static readonly TimeSpan ChangedTtl = TimeSpan.FromSeconds(15);

    private Dictionary<string, int> _changedWords = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _changedAt = DateTime.MinValue;

    /// <summary>Identifiers (with counts) from the files git reports as changed; refreshed at most every few seconds.</summary>
    private Dictionary<string, int> ChangedWords()
    {
        if (DateTime.UtcNow - _changedAt < ChangedTtl)
        {
            return _changedWords;
        }

        _changedAt = DateTime.UtcNow;
        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var snapshot = new Codale.Git.GitRepository(projectRoot).GetSnapshotAsync().GetAwaiter().GetResult();
            foreach (var change in snapshot.Changes.Take(40))
            {
                var path = Path.Combine(projectRoot, change.Path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 200_000)
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             File.ReadAllText(path), @"[A-Za-z_][A-Za-z0-9_]{3,}"))
                {
                    words[m.Value] = words.GetValueOrDefault(m.Value) + 1;
                }
            }
        }
        catch (Exception)
        {
            // Not a repository, or a file mid-write: no completions this round.
        }

        return _changedWords = words;
    }

    private ComposerPrediction? CompleteFromChanges(PromptWords.Word word)
    {
        string? best = null;
        var bestCount = 0;
        foreach (var (candidate, count) in ChangedWords())
        {
            // One letter left is not worth a keystroke.
            if (candidate.Length < word.Length + 2 || !candidate.StartsWith(word.Text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best is null ? null : new ComposerPrediction(word.Start, word.Length, PromptWords.MatchCase(word.Text, best), IsCorrection: false);
    }

    private readonly Lock _gate = new();
    private (string Text, int Caret, Action<ComposerPrediction?> Done)? _pending;
    private bool _running;
    private CancellationTokenSource? _model;

    /// <summary>
    /// Predicts for <paramref name="text"/> with the caret at <paramref name="caret"/> in the background;
    /// <paramref name="done"/> runs on the thread pool, and only for the newest request.
    /// </summary>
    public void Request(string text, int caret, Action<ComposerPrediction?> done)
    {
        lock (_gate)
        {
            // A newer keystroke supersedes a model continuation still waiting for its pause or answer.
            _model?.Cancel();
            _model = null;
            _pending = (text, caret, done);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            (string Text, int Caret, Action<ComposerPrediction?> Done) next;
            lock (_gate)
            {
                if (_pending is not { } pending)
                {
                    _running = false;
                    return;
                }

                next = pending;
                _pending = null;
            }

            ComposerPrediction? prediction = null;
            try
            {
                prediction = Predict(next.Text, next.Caret);
            }
            catch (Exception)
            {
                // A prediction is a nicety: an index mid-reload or a COM hiccup just means none this time.
            }

            bool stale;
            lock (_gate)
            {
                stale = _pending is not null;
            }

            if (stale)
            {
                continue;
            }

            next.Done(prediction);
        }
    }

    public ComposerPrediction? Predict(string text, int caret)
    {
        var index = SourceIndex.For(projectRoot);
        index.Warm();
        var model = index.Model;

        // Typing a word: complete it.
        if (PromptWords.Before(text, caret) is { } word)
        {
            // Words from the files changed in git complete first; no model completion.
            if (word.Length >= MinCompletionPrefix && CompleteFromChanges(word) is { } completion)
            {
                return completion;
            }

            return word.Length >= MinCorrectionLength ? Correct(model, word, typing: true) : null;
        }

        // Just finished a word with a space: check its spelling.
        if (caret >= 2 && caret <= text.Length && text[caret - 1] == ' ' &&
            PromptWords.Before(text, caret - 1) is { Length: >= MinCorrectionLength } finished)
        {
            return Correct(model, finished, typing: false);
        }

        return null;
    }

    /// <param name="typing">
    /// The caret is still in the word: it may be half typed, so only the workspace's own words are offered,
    /// and never a shorter one. The dictionary's fixes wait until the word is finished.
    /// </param>
    private static ComposerPrediction? Correct(WorkspaceModel model, PromptWords.Word word, bool typing)
    {
        if (model.Knows(word.Text) || word.Text.Any(char.IsDigit) || word.Text.Contains('_'))
        {
            return null;
        }

        if (typing)
        {
            // A real word the code just does not use ("laggy") is left alone; only a slip of one key is fixed.
            if (SystemSpelling.IsCorrect(word.Text))
            {
                return null;
            }

            var near = model.Correct(word.Text, maxEdits: 1).FirstOrDefault(w => w.Text.Length >= word.Length);
            return near is null
                ? null
                : new ComposerPrediction(word.Start, word.Length, near.Kind is null ? PromptWords.MatchCase(word.Text, near.Text) : near.Text, IsCorrection: true);
        }

        if (SystemSpelling.IsCorrect(word.Text))
        {
            return null;
        }

        // The workspace's spelling first: that is the name the agent will find.
        var system = SystemSpelling.Suggest(word.Text);
        var fromWorkspace = model.Correct(word.Text);
        var pick = fromWorkspace.FirstOrDefault(w => system.Contains(w.Text, StringComparer.OrdinalIgnoreCase)) ?? fromWorkspace.FirstOrDefault();

        var replacement = pick is not null
            ? (pick.Kind is null ? PromptWords.MatchCase(word.Text, pick.Text) : pick.Text)
            : system.FirstOrDefault();

        if (replacement is null || string.Equals(replacement, word.Text, StringComparison.Ordinal))
        {
            return null;
        }

        return new ComposerPrediction(word.Start, word.Length, replacement, IsCorrection: true);
    }
}
