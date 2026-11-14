using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class PromptVocabularyTests
{
    [Fact]
    public void A_completion_never_recases_what_was_typed()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("src/Codale.Search/SourceIndex.cs", VocabularySource.FileName);

        // "sour" must not become "SourceIndex"; the plain part "source" keeps the typed case.
        Assert.Equal("source", vocabulary.Complete("sour", null));
        Assert.Equal("SourceIndex", vocabulary.Complete("SourceI", null));
    }

    [Fact]
    public void A_plain_word_follows_the_typed_case()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("The prediction should compile.", VocabularySource.Assistant);

        Assert.Equal("Prediction", vocabulary.Complete("Pred", null));
        Assert.Equal("prediction", vocabulary.Complete("pred", null));
    }

    [Fact]
    public void Sentence_case_does_not_leak_into_completions()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("Composer text is ready.", VocabularySource.Assistant);

        Assert.Equal("composer", vocabulary.Complete("comp", null));
    }

    [Fact]
    public void The_previous_word_steers_the_completion()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("fix the ghost text and the ghost suggestion", VocabularySource.User);
        vocabulary.Add("text tests texture textual", VocabularySource.Assistant);

        Assert.Equal("suggestion", vocabulary.Complete("su", "ghost"));
    }

    [Fact]
    public void Recent_session_words_outrank_older_ones()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("the parser handles it", VocabularySource.Assistant);
        vocabulary.Add("the panel should collapse", VocabularySource.Assistant);

        Assert.Equal("panel", vocabulary.Complete("pa", null));
    }

    [Fact]
    public void The_next_word_is_offered_only_when_it_clearly_follows()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("open the composer predictor", VocabularySource.User);
        vocabulary.Add("the composer predictor is slow", VocabularySource.User);

        Assert.Equal("predictor", vocabulary.NextWord("composer"));
        Assert.Null(vocabulary.NextWord("slow"));
    }

    [Fact]
    public void Session_words_correct_spelling_but_a_single_user_typo_does_not()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("ComposerPredictor.cs", VocabularySource.FileName);
        vocabulary.Add("plese fix it", VocabularySource.User);

        Assert.Equal("ComposerPredictor", vocabulary.Near("ComposerPredicter", 2)[0].Text);
        Assert.False(vocabulary.Knows("plese"));
        Assert.True(vocabulary.Knows("composer"));
    }

    [Fact]
    public void Code_words_and_their_parts_are_learned()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("var ghost = new GhostTextRenderer();", VocabularySource.Code);

        Assert.True(vocabulary.Knows("GhostTextRenderer"));
        Assert.Equal("renderer", vocabulary.Complete("rend", null));
    }

    [Fact]
    public void General_language_fills_in_words_the_session_never_used()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("fix the program", VocabularySource.Assistant);

        Assert.Equal("build", vocabulary.Complete("bui", "to", null, ["build", "by I", "VUI"]));
        Assert.Equal("Correct", vocabulary.Complete("Corr", "build", null, ["for r", "correct", "core"]));
    }

    [Fact]
    public void A_strong_session_word_beats_the_general_guess()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("src/ComposerPredictor.cs", VocabularySource.FileName);

        Assert.Equal("ComposerPredictor", vocabulary.Complete("Comp", null, null, ["Company", "Computer"]));
        Assert.Equal("composer", vocabulary.Complete("comp", null, null, ["company", "computer"]));
    }

    [Fact]
    public void A_general_guess_the_session_used_outranks_the_top_guess()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("the composer is slow", VocabularySource.Assistant);

        Assert.Equal("composer", vocabulary.Complete("comp", null, null, ["company", "composer"]));
    }

    [Fact]
    public void Clear_forgets_the_session()
    {
        var vocabulary = new PromptVocabulary();
        vocabulary.Add("composer", VocabularySource.Assistant);
        vocabulary.Clear();

        Assert.Null(vocabulary.Complete("comp", null));
    }
}
