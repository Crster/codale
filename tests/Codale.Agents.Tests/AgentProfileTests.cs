using System.Text.Json;
using Codale.App.Services;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class AgentProfileTests
{
    [Fact]
    public void Account_model_choices_are_independent_and_pro_falls_back_to_flash()
    {
        var first = new ByokProvider { IsAccount = true, FlashModel = "fast-a", ProModel = "pro-a" };
        var second = new ByokProvider { IsAccount = true, FlashModel = "fast-b" };
        Assert.Equal("fast-a", first.ModelForTier("flash"));
        Assert.Equal("pro-a", first.ModelForTier("pro"));
        Assert.Equal("fast-b", second.ModelForTier("flash"));
        Assert.Equal("fast-b", second.ModelForTier("pro"));
        Assert.Null(new ByokProvider { IsAccount = true }.ModelForTier("flash"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void AccountProfilesAreRestrictedToTheirCli(string cli)
    {
        var profile = new ByokProvider { CliId = cli, IsAccount = true };
        Assert.True(profile.Supports(cli));
        Assert.False(profile.Supports(cli == "claude" ? "codex" : "claude"));
    }

    [Fact]
    public void LegacyEndpointSupportsOnlyConfiguredProtocols()
    {
        var profile = new ByokProvider { Name = "Old endpoint", BaseUrl = "http://localhost:11434/v1" };
        Assert.True(profile.Supports("codex"));
        Assert.False(profile.Supports("claude"));
        Assert.Equal("Old endpoint", profile.Key);
        profile.AnthropicBaseUrl = "http://localhost:11434";
        Assert.True(profile.Supports("claude"));
    }

    [Fact]
    public void IdentitySurvivesRenameAndRoundTrip()
    {
        var profile = new ByokProvider { Id = Guid.NewGuid().ToString("N"), Name = "Ollama", CliId = "codex", IsAccount = true, AccountId = Guid.NewGuid().ToString("N"), Email = "user@example.com" };
        var key = profile.Key;
        profile.Name = "Another label";
        var restored = JsonSerializer.Deserialize<ByokProvider>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(key, restored.Key);
        Assert.Equal(profile.AccountId, restored.AccountId);
        Assert.Equal(profile.Email, restored.Email);
        Assert.True(restored.IsAccount);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData(@"C:\other")]
    [InlineData("invalid")]
    public void AccountDirectoryRejectsInvalidIds(string id) =>
        Assert.Throws<ArgumentException>(() => CodaleAccount.ProfileHome("codex", id));

    [Fact]
    public void AccountDirectoryRejectsUnknownCli() =>
        Assert.Throws<ArgumentException>(() => CodaleAccount.ProfileHome("other"));
}
