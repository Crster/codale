using Codale.Agents.Agy;

namespace Codale.Agents.Tests;

/// <summary>The terminal panel's Antigravity meter, read from a transcript shaped like the CLI writes it.</summary>
public sealed class AgyContextProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-agy-probe-" + Guid.NewGuid().ToString("N"));

    public AgyContextProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Planner(int input, int cache) =>
        $$$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","input_tokens":{{{{input}}}},"cache_read_tokens":{{{{cache}}}},"output_tokens":50}""";

    [Fact]
    public void Reads_the_last_planner_tokens_and_window()
    {
        var historyFile = Path.Combine(_root, "history.jsonl");
        var convId = "test-conv-123";
        File.WriteAllLines(historyFile, [
            $$$$"""{"display":"hello","workspace":"X:\\myproject","conversationId":"{{{{convId}}}}"}"""
        ]);

        var brainDir = Path.Combine(_root, "brain", convId, ".system_generated", "logs");
        Directory.CreateDirectory(brainDir);
        File.WriteAllLines(Path.Combine(brainDir, "transcript.jsonl"), [
            Planner(1000, 5000),
            Planner(2000, 15000)
        ]);

        var reading = AgyContextProbe.Read(historyFile, "X:\\myproject", DateTime.UtcNow.AddMinutes(-5));

        Assert.NotNull(reading);
        Assert.Equal(17000, reading.Value.ContextTokens);
        Assert.Equal(1_000_000, reading.Value.ContextWindow);
    }

    [Fact]
    public void Pro_model_gets_2M_window()
    {
        Assert.Equal(2_000_000, AgyContextProbe.WindowFor("Gemini 3.8 Pro (High)"));
        Assert.Equal(1_000_000, AgyContextProbe.WindowFor("Gemini 3.8 Flash (High)"));
        Assert.Equal(1_000_000, AgyContextProbe.WindowFor(null));
    }
}
