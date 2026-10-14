using Codale.Core.Features;

namespace Codale.Core.Tests;

public sealed class FeatureSearchTests
{
    [Fact]
    public void Identifiers_are_found_by_their_parts()
    {
        var tokens = SearchText.Tokens("LoginService.refresh_token HTTPServer").ToList();

        Assert.Contains("loginservice", tokens);
        Assert.Contains("login", tokens);
        Assert.Contains("service", tokens);
        Assert.Contains("refresh", tokens);
        Assert.Contains("token", tokens);
        Assert.Contains("http", tokens);
        Assert.Contains("server", tokens);
    }

    [Fact]
    public void A_query_drops_filler_and_quotes_each_token()
    {
        var query = SearchText.ToMatchQuery("please fix the login redirect")!;

        Assert.Contains("\"login\"", query);
        Assert.Contains("\"redirect\"", query);
        Assert.DoesNotContain("\"the\"", query);
        Assert.DoesNotContain("\"please\"", query);
    }

    [Fact]
    public void Text_with_nothing_to_match_has_no_query()
    {
        Assert.Null(SearchText.ToMatchQuery("the and to"));
    }

    [Fact]
    public void A_file_is_cut_into_overlapping_chunks_that_cover_it()
    {
        var content = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var chunks = FileChunker.Chunk(content);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Contains("line 100", chunks[^1].Text);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= FileChunker.MaxChars + 50));
        Assert.True(chunks[1].StartLine < 41, "chunks overlap");
    }

    [Fact]
    public void Binary_and_oversized_content_is_not_indexable()
    {
        Assert.False(FileChunker.IsIndexable("a\0b"));
        Assert.False(FileChunker.IsIndexable(new string('x', FileChunker.MaxFileBytes + 1)));
        Assert.False(FileChunker.IsIndexable(""));
        Assert.True(FileChunker.IsIndexable("class A {}"));
    }
}
