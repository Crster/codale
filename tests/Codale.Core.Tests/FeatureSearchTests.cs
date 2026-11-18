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
}
