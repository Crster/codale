namespace Codale.Settings.Tests;

public sealed class ModelPreferenceTests
{
    [Fact]
    public void SavingATierPublishesOneCompleteSelectionAndKeepsTheLatestChoice()
    {
        var root = Directory.CreateTempSubdirectory("codale-tier-preference-").FullName;
        using (var settings = new AppSettingsStore(root))
        {
            settings.SetModelTierPreference("codex", "plus", "capable-model", "high");
            var changes = 0;
            settings.Changed += (_, _) =>
            {
                changes++;
                Assert.Equal("cli", settings.DefaultTierFor("codex"));
                Assert.Equal((true, (string?)null, (string?)null), settings.ModelPreferenceFor("codex"));
            };
            settings.SetModelTierPreference("codex", "cli", null, null);
            settings.SetModelTierPreference("codex", "cli", null, null);
            Assert.Equal(1, changes);
        }

        using var reopened = new AppSettingsStore(root);
        Assert.Equal("cli", reopened.DefaultTierFor("codex"));
        Assert.Equal((true, (string?)null, (string?)null), reopened.ModelPreferenceFor("codex"));
    }

    [Fact]
    public void ExplicitChoiceSurvivesReopenAndProfileChanges()
    {
        var root = Directory.CreateTempSubdirectory("codale-model-preference-").FullName;
        using (var settings = new AppSettingsStore(root))
        {
            Assert.False(settings.ModelPreferenceFor("claude").HasValue);
            settings.SetDefaultTier("claude", "plus");
            settings.SetModelPreference("claude", "claude-opus-test", "high");
            settings.RememberProfile("claude", "another-account");
            settings.SetModelPreference("codex", "gpt-test", "medium");
            Assert.Equal((true, "claude-opus-test", "high"), settings.ModelPreferenceFor("claude"));
        }
        using var reopened = new AppSettingsStore(root);
        Assert.Equal((true, "claude-opus-test", "high"), reopened.ModelPreferenceFor("claude"));
        Assert.Equal((true, "gpt-test", "medium"), reopened.ModelPreferenceFor("codex"));
        Assert.Equal("plus", reopened.DefaultTierFor("claude"));
    }

    [Fact]
    public void ExplicitCliDefaultIsDistinctFromAnUnsetPreference()
    {
        var root = Directory.CreateTempSubdirectory("codale-model-default-").FullName;
        using var settings = new AppSettingsStore(root);
        settings.SetModelPreference("claude", "opus", "high");
        settings.SetModelPreference("claude", null, null);
        Assert.Equal((true, (string?)null, (string?)null), settings.ModelPreferenceFor("claude"));
        Assert.False(settings.ModelPreferenceFor("codex").HasValue);
        settings.SetDefaultMode("claude", "plan");
        Assert.Equal("plan", settings.DefaultModeFor("claude"));
    }
}
