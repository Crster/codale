namespace Codale.Agents.Tests;

public sealed class ByokProviderTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", " qwen-flash ", "qwen-max", "qwen-flash")]
    [InlineData("claude-sonnet-4-6", "qwen-flash", "qwen-max", "qwen-flash")]
    [InlineData(null, "qwen-flash", "qwen-max", "qwen-flash")]
    [InlineData("QWEN-MAX", "qwen-flash", " qwen-max ", "qwen-max")]
    [InlineData(null, "", "qwen-max", "qwen-max")]
    [InlineData("gpt-6.1-sol", " ", "", null)]
    public void Model_selection_stays_within_the_endpoint_profile(
        string? requested, string flash, string pro, string? expected)
    {
        var provider = new ByokProvider { FlashModel = flash, ProModel = pro };

        Assert.Equal(expected, provider.ResolveEndpointModel(requested));
    }
}
