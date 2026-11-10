using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class SessionBriefingTests
{
    [Fact]
    public void The_context_block_is_stripped_back_to_the_users_words()
    {
        var sent = new UserTurn("now add tests") { ContextPrefix = SessionBriefing.ContextBlock("## Goal\nShip it") }.ProviderText;

        Assert.Contains("Ship it", sent);
        Assert.Equal("now add tests", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void The_context_block_and_the_ask_prefix_strip_together()
    {
        var sent = new UserTurn("why?") { ContextPrefix = SessionBriefing.ContextBlock("brief"), AskOnly = true }.ProviderText;

        Assert.Equal("why?", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void The_context_block_tells_every_provider_not_to_re_search_verified_context()
    {
        var block = SessionBriefing.ContextBlock("db/connectors.ts");
        Assert.Contains("verified current", block);
        Assert.Contains("historical", block);
        Assert.Contains("do not grep, glob, search or re-read", block);
        Assert.Contains("read only the listed line range", block);
        Assert.DoesNotContain("not instructions", block);
    }

    [Fact]
    public void Follow_up_briefings_are_short_and_stable_for_the_prompt_cache()
    {
        var first = SessionBriefing.ContextBlock("a");
        var later = SessionBriefing.ContextBlock("a", first: false);
        Assert.True(later.Length < first.Length / 2);
        Assert.Equal(later, SessionBriefing.ContextBlock("a", first: false));
        Assert.Equal("q", SessionBriefing.StripContext(later + "q"));
    }

    [Fact]
    public void Retrieval_terms_drop_question_filler_and_meet_inflections()
    {
        var terms = Codale.Core.Features.SearchText.RetrievalTerms("where does the new connector table located?");
        Assert.Equal(["connector", "table"], terms);
        Assert.Equal("connector", Codale.Core.Features.SearchText.Stem("connectors"));
        Assert.Equal("index", Codale.Core.Features.SearchText.Stem("indexes"));
        Assert.Equal("status", Codale.Core.Features.SearchText.Stem("status"));
        Assert.Equal("\"connector\"* OR \"table\"*", Codale.Core.Features.SearchText.ToRetrievalQuery("connectors tables"));
    }

    [Fact]
    public void Hashed_embedding_is_deterministic_normalised_and_inflection_tolerant()
    {
        var e = Codale.Core.Features.HashedTextEmbedder.Instance;
        var a = e.Embed("connector table");
        Assert.Equal(a, e.Embed("connector table"));
        Assert.Equal(1f, Codale.Core.Features.VectorMath.Dot(a, a), 3);
        var near = Codale.Core.Features.VectorMath.Dot(a, e.Embed("CREATE TABLE connectors"));
        var far = Codale.Core.Features.VectorMath.Dot(a, e.Embed("render revenue graph"));
        Assert.True(near > far + 0.3f, $"near={near} far={far}");
        Assert.Equal(a, Codale.Core.Features.VectorMath.FromBytes(Codale.Core.Features.VectorMath.ToBytes(a)));
    }

    [Fact]
    public void Text_without_a_context_block_is_untouched()
    {
        Assert.Equal("plain", SessionBriefing.StripContext("plain"));
    }
}
