using System.Globalization;
using Windows.Data.Text;

namespace Codale.App.Services;

/// <summary>
/// The Windows text prediction engine (the one the touch keyboard suggests from), asked for word
/// completions given the words before - so plain English the session never used ("build",
/// "correctly") still completes, in context. Created on first use, off the UI thread; every call is
/// serialized. Unavailable (no language data, WinRT failure) means it predicts nothing.
/// </summary>
internal static class SystemPrediction
{
    private const int MaxPreviousWords = 5;

    private static readonly Lock Gate = new();
    private static TextPredictionGenerator? _generator;
    private static bool _tried;

    /// <summary>Candidates for the word starting <paramref name="prefix"/>, best first; empty when the engine is unavailable.</summary>
    /// <param name="previous">The words before it, oldest first; only the last few are used.</param>
    public static IReadOnlyList<string> Complete(string prefix, IReadOnlyList<string> previous, int max = 6)
    {
        lock (Gate)
        {
            if (Generator() is not { } generator)
            {
                return [];
            }

            try
            {
                var context = previous.Count > MaxPreviousWords ? previous.Skip(previous.Count - MaxPreviousWords) : previous;
                return generator
                    .GetCandidatesAsync(prefix, (uint)max, TextPredictionOptions.Predictions, context)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    private static TextPredictionGenerator? Generator()
    {
        if (_tried)
        {
            return _generator;
        }

        _tried = true;
        foreach (var tag in new[] { CultureInfo.CurrentUICulture.Name, "en-US" })
        {
            if (tag.Length == 0)
            {
                continue;
            }

            try
            {
                var generator = new TextPredictionGenerator(tag);
                if (!generator.LanguageAvailableButNotInstalled)
                {
                    _generator = generator;
                    break;
                }
            }
            catch (Exception)
            {
                // Not a language Windows predicts for: try the next.
            }
        }

        return _generator;
    }
}
