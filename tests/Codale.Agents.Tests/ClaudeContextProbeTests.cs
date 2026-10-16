using Codale.Agents.Claude;

namespace Codale.Agents.Tests;

/// <summary>The terminal panel's Claude meter, read from a transcript shaped like the CLI writes it.</summary>
public sealed class ClaudeContextProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-claude-probe-" + Guid.NewGuid().ToString("N"));

    public ClaudeContextProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Assistant(int cacheRead, bool sidechain = false) =>
        $$$$"""{"type":"assistant","isSidechain":{{{{(sidechain ? "true" : "false")}}}},"requestedModel":"claude-sonnet-5-5","message":{"model":"claude-sonnet-5-5","usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":{{{{cacheRead}}}},"output_tokens":40}}}""";

    [Fact]
    public void Reads_the_last_prompts_tokens_and_model_skipping_subagents()
    {
        File.WriteAllLines(Path.Combine(_root, "session.jsonl"), [Assistant(80_000), Assistant(150_000), Assistant(5_000, sidechain: true)]);

        var reading = ClaudeContextProbe.Read(_root, DateTime.UtcNow.AddMinutes(-5));

        Assert.NotNull(reading);
        Assert.Equal(150_102, reading.Value.Tokens);
        Assert.Equal("claude-sonnet-5-5", reading.Value.Model);
    }

    [Fact]
    public void A_known_window_beats_the_200K_guess_and_a_session_past_200K_beats_both()
    {
        Assert.Equal(200_000, ClaudeContextProbe.WindowFor(50_000));
        Assert.Equal(1_000_000, ClaudeContextProbe.WindowFor(50_000, known: 1_000_000));
        Assert.Equal(1_000_000, ClaudeContextProbe.WindowFor(250_000, known: 200_000));
    }

    [Fact]
    public void A_1m_pick_is_remembered_under_the_bare_model_id_the_transcript_logs()
    {
        Assert.Equal("claude-fable-5-1", ClaudeContextProbe.ModelKey("claude-fable-5-1[1m]"));
        Assert.Equal("claude-sonnet-5-5", ClaudeContextProbe.ModelKey("Claude-Sonnet-5-5"));
    }
}
