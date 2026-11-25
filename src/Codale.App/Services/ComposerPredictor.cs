using System.Collections.Concurrent;

using Codale.Core.Text;
using Codale.Search;
using Codale.Search.Engine;

namespace Codale.App.Services;

/// <summary>What the composer offers at the caret: replace a span of the prompt with <see cref="Replacement"/>.</summary>
/// <param name="IsCorrection">A spelling fix for a finished word, rather than the rest of the word (or the next word) being typed.</param>
internal sealed record ComposerPrediction(int Start, int Length, string Replacement, bool IsCorrection)
{
    public int End => Start + Length;
}

/// <summary>
/// Predicts from this chat session: the words the user and the agent wrote, the files the agent read
/// and edited, and the code it added (<see cref="PromptVocabulary"/>), so the ghost text follows the
/// topic of the conversation. Plain English the session never used completes from the Windows text
/// prediction engine, given the words before (<see cref="SystemPrediction"/>), with the session's
/// words ranked first. Completions only ever extend what was typed - the ghost shows exactly
/// what Tab inserts. A finished word that neither the session, the workspace nor the Windows
/// dictionary knows gets the closest spelling, the session's first.
/// </summary>
/// <remarks>
/// Everything runs off the UI thread on one worker: <see cref="Feed"/> queues session text, which the
/// worker learns before the next prediction, and <see cref="Request"/> keeps one prediction in flight
/// and drops requests the user has already typed past.
/// </remarks>
internal sealed class ComposerPredictor(string projectRoot)
{
    private const int MinCorrectionLength = 3;

    private readonly PromptVocabulary _vocabulary = new();
    private readonly ConcurrentQueue<(string? Text, VocabularySource Source)> _feed = new();

    private readonly Lock _gate = new();
    private (string Text, int Caret, Action<ComposerPrediction?> Done)? _pending;
    private bool _running;

    /// <summary>Queues session text to learn from; it is read on the worker, never on the caller's thread.</summary>
    public void Feed(string text, VocabularySource source)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _feed.Enqueue((text, source));
            Kick();
        }
    }

    /// <summary>Forgets the session's words: the conversation was cleared.</summary>
    public void ResetSession()
    {
        _feed.Enqueue((null, default));
        Kick();
    }

    /// <summary>
    /// Predicts for <paramref name="text"/> with the caret at <paramref name="caret"/> in the background;
    /// <paramref name="done"/> runs on the thread pool, and only for the newest request.
    /// </summary>
    public void Request(string text, int caret, Action<ComposerPrediction?> done)
    {
        lock (_gate)
        {
            _pending = (text, caret, done);
        }

        Kick();
    }

    private void Kick()
    {
        lock (_gate)
        {
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
            Learn();

            (string Text, int Caret, Action<ComposerPrediction?> Done) next;
            lock (_gate)
            {
                if (_pending is not { } pending)
                {
                    if (_feed.IsEmpty)
                    {
                        _running = false;
                        return;
                    }

                    continue;
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

            if (!stale)
            {
                next.Done(prediction);
            }
        }
    }

    private void Learn()
    {
        while (_feed.TryDequeue(out var item))
        {
            if (item.Text is null)
            {
                _vocabulary.Clear();
            }
            else
            {
                _vocabulary.Add(item.Text, item.Source);
            }
        }
    }

    private ComposerPrediction? Predict(string text, int caret)
    {
        // Typing a word: complete it from the session, or fix a slip of one key.
        if (PromptWords.Before(text, caret) is { } word)
        {
            var previous = PromptWords.Previous(text, word.Start);
            var draft = text[..word.Start] + " " + text[word.End..];

            // The session knows the topic; the system's language model knows English and the phrase so far.
            var general = word.Length >= 2 ? SystemPrediction.Complete(word.Text, WordsBefore(text, word.Start)) : [];
            return _vocabulary.Complete(word.Text, previous, draft, general) is { } completion
                ? new ComposerPrediction(word.Start, word.Length, completion, IsCorrection: false)
                : word.Length >= MinCorrectionLength ? Correct(word, previous, typing: true) : null;
        }

        // Just finished a word with a space: check its spelling, else offer the word that usually follows.
        if (caret >= 2 && caret <= text.Length && text[caret - 1] == ' ' &&
            PromptWords.Before(text, caret - 1) is { } finished)
        {
            if (finished.Length >= MinCorrectionLength &&
                Correct(finished, PromptWords.Previous(text, finished.Start), typing: false) is { } fix)
            {
                return fix;
            }

            if (_vocabulary.NextWord(finished.Text) is { } next)
            {
                return new ComposerPrediction(caret, 0, next, IsCorrection: false);
            }
        }

        return null;
    }

    /// <param name="typing">
    /// The caret is still in the word: it may be half typed, so only a slip of one key is fixed, and
    /// never to a shorter word. The dictionary's fixes wait until the word is finished.
    /// </param>
    private ComposerPrediction? Correct(PromptWords.Word word, string? previous, bool typing)
    {
        if (word.Text.Any(char.IsDigit) || word.Text.Contains('_') || _vocabulary.Knows(word.Text))
        {
            return null;
        }

        var model = Workspace();
        if (model?.Knows(word.Text) == true || SystemSpelling.IsCorrect(word.Text))
        {
            return null;
        }

        string? replacement;
        if (typing)
        {
            // A real word the session never used ("laggy") is left alone above; only a one-key slip is fixed.
            replacement = _vocabulary.Near(word.Text, maxEdits: 1, previous)
                .Select(n => n.Text)
                .FirstOrDefault(t => t.Length >= word.Length);
            replacement ??= model?.Correct(word.Text, maxEdits: 1)
                .Where(w => w.Text.Length >= word.Length)
                .Select(w => w.Text)
                .FirstOrDefault();
        }
        else
        {
            replacement = PickCorrection(word.Text, previous, model);
        }

        if (replacement is null)
        {
            return null;
        }

        replacement = Recase(word.Text, replacement);
        return string.Equals(replacement, word.Text, StringComparison.Ordinal)
            ? null
            : new ComposerPrediction(word.Start, word.Length, replacement, IsCorrection: true);
    }

    /// <summary>
    /// The session's spelling first (that is what the conversation is about), then the dictionary's
    /// suggestion the session or workspace uses most, then the workspace's, then the dictionary's own pick.
    /// </summary>
    private string? PickCorrection(string word, string? previous, WorkspaceModel? model)
    {
        var system = SystemSpelling.Suggest(word, max: 5);
        var near = _vocabulary.Near(word, maxEdits: 2, previous);
        if (near.Count > 0)
        {
            var closest = near[0].Distance;
            return near.Where(n => n.Distance == closest)
                       .Select(n => n.Text)
                       .FirstOrDefault(t => system.Contains(t, StringComparer.OrdinalIgnoreCase))
                   ?? near[0].Text;
        }

        // Rank the dictionary's guesses by how much this conversation talks about them.
        var contextual = system
            .Select((s, i) => (Text: s, Order: i, Score: _vocabulary.Relevance(s, previous)))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Order)
            .Select(s => s.Text)
            .FirstOrDefault();
        if (contextual is not null)
        {
            return contextual;
        }

        var fromWorkspace = model?.Correct(word) ?? [];
        return fromWorkspace.FirstOrDefault(w => system.Contains(w.Text, StringComparer.OrdinalIgnoreCase))?.Text
               ?? system.FirstOrDefault()
               ?? fromWorkspace.FirstOrDefault()?.Text;
    }

    /// <summary>The prose words of the current sentence before <paramref name="end"/>, oldest first, as context for the language model.</summary>
    private static List<string> WordsBefore(string text, int end)
    {
        var start = end;
        // A dot ends the sentence only before a space: "Program.cs" is one word.
        while (start > 0 && text[start - 1] is not ('!' or '?' or '\n') &&
               !(text[start - 1] == '.' && start < text.Length && char.IsWhiteSpace(text[start])))
        {
            start--;
        }

        var words = new List<string>();
        foreach (var token in text[start..end].Split([' ', '\t', ',', ';', ':', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Any(char.IsLetter))
            {
                words.Add(token);
            }
        }

        return words;
    }

    /// <summary>A plain word takes the casing the user typed; a name ("ChatTab") keeps its own.</summary>
    private static string Recase(string typed, string replacement) => PromptWords.MatchCase(typed, replacement);

    /// <summary>The workspace model, once the index has it loaded; never waits for a cold index to build.</summary>
    private WorkspaceModel? Workspace()
    {
        try
        {
            var index = SourceIndex.For(projectRoot);
            return index.Model;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
