using Codale.Agents.Claude;
using Codale.Agents.Codex;

namespace Codale.Agents.Tests;

public class AgentSessionFactoryTests
{
    [Theory]
    [InlineData("claude", typeof(ClaudeAgentSession))]
    [InlineData("codex", typeof(CodexAgentSession))]
    [InlineData("CODEX", typeof(CodexAgentSession))]
    public async Task CreatesTheRequestedProviderWithoutStartingAProcess(string cli, Type expected)
    {
        IAgentSessionFactory factory = new AgentSessionFactory();
        await using var session = factory.Create(new AgentSessionRequest { CliId = cli, WorkingDirectory = Path.GetTempPath() });
        Assert.IsType(expected, session);
    }

    [Fact]
    public void RejectsUnknownProviders()
    {
        IAgentSessionFactory factory = new AgentSessionFactory();
        Assert.Throws<ArgumentException>(() => factory.Create(new AgentSessionRequest { CliId = "other", WorkingDirectory = Path.GetTempPath() }));
    }
}