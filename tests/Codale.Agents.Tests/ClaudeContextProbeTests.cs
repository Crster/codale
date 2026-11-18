using Codale.Agents.Claude;

namespace Codale.Agents.Tests;

public sealed class ClaudeContextProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-claude-probe-" + Guid.NewGuid().ToString("N"));

    public ClaudeContextProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Reads_only_the_requested_terminals_report()
    {
        var path = Path.Combine(_root, "current.json");
        File.WriteAllText(Path.Combine(_root, "other.json"), """{"tokens":150102,"window":200000}""");
        Assert.Null(ClaudeContextProbe.Read(path));

        File.WriteAllText(path, """{"tokens":150102,"window":1000000}""");
        Assert.Equal(new ClaudeContextReading(150102, 1000000), ClaudeContextProbe.Read(path));

        File.WriteAllText(path, """{"tokens":0,"window":200000}""");
        Assert.Equal(new ClaudeContextReading(0, 200000), ClaudeContextProbe.Read(path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"tokens\":100}")]
    public void Incomplete_readings_do_not_supply_a_limit(string json)
    {
        var path = Path.Combine(_root, "current.json");
        File.WriteAllText(path, json);
        Assert.Null(ClaudeContextProbe.Read(path));
    }
}
