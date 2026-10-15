using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class FeatureSummarizerTests
{
    [Theory]
    [InlineData(100, 1, false)]
    [InlineData(FeatureSummarizer.UpdateBytes, 1, true)]
    [InlineData(100, FeatureSummarizer.UpdateTurns, true)]
    public void A_summary_is_due_after_enough_bytes_or_turns(long bytes, int turns, bool due)
    {
        Assert.Equal(due, FeatureSummarizer.IsDue(bytes, turns));
    }

    [Fact]
    public void The_input_fences_the_previous_summary_and_the_new_progress_as_data()
    {
        var input = FeatureSummarizer.BuildInput("Login", "## Done\nsign in works", [new HandoffEntry(HandoffRole.User, "now fix refresh")]);

        Assert.Contains("<feature>\nLogin\n</feature>", input);
        Assert.Contains("<previous_summary>", input);
        Assert.Contains("sign in works", input);
        Assert.Contains("<new_progress>", input);
        Assert.Contains("User: now fix refresh", input);
    }

    [Fact]
    public void A_first_summary_says_there_is_no_previous_one()
    {
        Assert.Contains("(none yet)", FeatureSummarizer.BuildInput("Login", "", []));
    }

    [Fact]
    public void The_seed_context_carries_summary_progress_files_and_code_within_budget()
    {
        var feature = new FeatureInfo { Id = "f", Name = "Login", Summary = "## Done\nsign in works" };
        var code = Enumerable.Range(1, 20)
            .Select(i => new FileHit("f", $"src/File{i}.cs", i, new string('x', 900)))
            .ToList();

        var seed = FeatureContext.Build(feature, [new HandoffEntry(HandoffRole.User, "fix refresh")], ["src/Auth.cs"], code);

        Assert.Contains("# Feature: Login", seed);
        Assert.Contains("sign in works", seed);
        Assert.Contains("Progress since that summary", seed);
        Assert.Contains("- `src/Auth.cs`", seed);
        Assert.Contains("src/File1.cs:1", seed);
        Assert.DoesNotContain("src/File20.cs", seed);
        Assert.True(seed.Length < FeatureContext.CodeBudgetChars + 2_000);
    }

    [Fact]
    public void A_feature_with_no_summary_is_described_by_its_description()
    {
        var seed = FeatureContext.Build(new FeatureInfo { Id = "f", Name = "Login", Description = "Sign in flow" }, [], [], []);

        Assert.Contains("Sign in flow", seed);
    }
}