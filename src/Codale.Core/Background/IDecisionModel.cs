namespace Codale.Core.Background;

/// <summary>One allowed answer to a <see cref="DecisionQuestion"/>.</summary>
public sealed record DecisionOption(string Key, string Description);

/// <summary>A closed question a decision model answers by picking one of <see cref="Options"/>.</summary>
/// <param name="Id">The answer's key in the reply.</param>
public sealed record DecisionQuestion(string Id, string Instructions, IReadOnlyList<DecisionOption> Options);

/// <summary>The option a decision model picked, how sure it is, and what it gave every option.</summary>
public sealed record DecisionAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities);

/// <summary>
/// A model built to decide rather than write: it reads a state and scores each question's options in one
/// pass, without generating text, so a decision costs a fraction of a chat turn. Ollama serves these as
/// models with the "decision" capability.
/// </summary>
public interface IDecisionModel
{
    string Model { get; }

    /// <summary>The answer to every question, by <see cref="DecisionQuestion.Id"/>.</summary>
    /// <param name="state">What the decision is about, as named text fields.</param>
    /// <exception cref="Helper.HelperModelException">The server could not be reached, or the model cannot decide.</exception>
    Task<IReadOnlyDictionary<string, DecisionAnswer>> DecideAsync(
        IReadOnlyDictionary<string, string> state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default);
}
