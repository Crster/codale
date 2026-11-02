namespace Codale.Search.Tests;

public sealed class SearchQueryIntentTests
{
    [Theory]
    [InlineData("RetryPolicy", false)]
    [InlineData("RetryPolcy", false)]
    [InlineData("get", false)]
    [InlineData("where.cs", false)]
    [InlineData("src/SearchQueryIntent.cs", false)]
    [InlineData("run build", false)]
    [InlineData("npm run build", false)]
    [InlineData("retry policy timeout backoff upload network errors", false)]
    [InlineData("\"what is this?\"", false)]
    [InlineData("where is RetryPolicy", true)]
    [InlineData("How does upload retry work?", true)]
    [InlineData("show me the upload handler", true)]
    [InlineData("cancel a running upload", true)]
    [InlineData("upload doesn't work", true)]
    [InlineData("retry when upload fails", true)]
    [InlineData("RetryPolicy?", true)]
    [InlineData("where is \"run build\" defined", true)]
    [InlineData("explain `RetryPolicy`", true)]
    [InlineData("find \"run build\"", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Detects_questions_and_sentences_without_reinterpreting_keywords(string query, bool expected)
    {
        Assert.Equal(expected, SearchQueryIntent.IsNaturalLanguage(query));
    }

    [Theory]
    [InlineData("get file", "get", "file")]
    [InlineData("set value", "set", "value")]
    [InlineData("app code", "app", "code")]
    [InlineData("IO UI", "IO", "UI")]
    public void Keyword_search_preserves_short_words_and_question_filler(string query, string first, string second)
    {
        var terms = SourceExplorer.QueryTerms(query);

        Assert.Equal([first, second], terms.Select(t => t.Text));
    }

    [Fact]
    public void Sentence_search_discards_question_words_and_generic_filler()
    {
        var terms = SourceExplorer.QueryTerms("how does the app get the snake speed");

        Assert.Equal(["snake", "speed"], terms.Select(t => t.Text));
    }

    [Fact]
    public void Keyword_search_keeps_every_word_in_a_long_keyword_list()
    {
        var terms = SourceExplorer.QueryTerms("retry policy timeout backoff upload network errors");

        Assert.Equal(7, terms.Count);
        Assert.Contains(terms, t => t.Text == "errors");
    }
}
