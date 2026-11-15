using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class ConnectPreflightTests
{
    [Fact]
    public void AnEndpointEnvironmentSkipsTheAccountCheck()
    {
        Assert.False(ConnectPreflight.NeedsAccountCheck(hasEndpointEnvironment: true, isCodex: false, environmentOverridesEndpoint: false));
        Assert.False(ConnectPreflight.NeedsAccountCheck(hasEndpointEnvironment: true, isCodex: true, environmentOverridesEndpoint: false));
    }

    [Fact]
    public void ClaudeWithAnAmbientOverrideSkipsTheAccountCheck()
    {
        Assert.False(ConnectPreflight.NeedsAccountCheck(hasEndpointEnvironment: false, isCodex: false, environmentOverridesEndpoint: true));
    }

    [Fact]
    public void CodexAlwaysChecksTheAccountWithoutAnEndpoint()
    {
        Assert.True(ConnectPreflight.NeedsAccountCheck(hasEndpointEnvironment: false, isCodex: true, environmentOverridesEndpoint: true));
    }

    [Fact]
    public void ClaudeWithoutAnOverrideChecksTheAccount()
    {
        Assert.True(ConnectPreflight.NeedsAccountCheck(hasEndpointEnvironment: false, isCodex: false, environmentOverridesEndpoint: false));
    }

    [Theory]
    [InlineData("No conversation found with session ID: abc")]
    [InlineData("error: no rollout found for thread")]
    [InlineData("Thread not found")]
    [InlineData("the thread was not found on disk")]
    public void AMissingConversationIsLostWhenResuming(string message) =>
        Assert.True(ConnectPreflight.IsResumeLost(hasResumeId: true, message));

    [Fact]
    public void AFailureWithoutAResumeIdIsNeverALostConversation() =>
        Assert.False(ConnectPreflight.IsResumeLost(hasResumeId: false, "No conversation found with session ID"));

    [Fact]
    public void AnUnrelatedFailureIsNotALostConversation() =>
        Assert.False(ConnectPreflight.IsResumeLost(hasResumeId: true, "Could not start: network unreachable"));

    [Fact]
    public void ThreadAloneIsNotEnoughWithoutNotFound() =>
        Assert.False(ConnectPreflight.IsResumeLost(hasResumeId: true, "thread pool exhausted"));
}