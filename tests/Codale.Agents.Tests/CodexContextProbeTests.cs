using Codale.Agents.Codex;

namespace Codale.Agents.Tests;

/// <summary>The terminal panel's Codex meter, read from a rollout shaped like the CLI writes it.</summary>
public sealed class CodexContextProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Rollout(string name, string originator, string cwd, params string[] events)
    {
        var day = Path.Combine(_root, "2026", "10", "14");
        Directory.CreateDirectory(day);
        var meta = $$$"""{"type":"session_meta","payload":{"originator":"{{{originator}}}","cwd":"{{{cwd.Replace("\\", "\\\\")}}}"}}""";
        File.WriteAllLines(Path.Combine(day, $"rollout-{name}.jsonl"), [meta, .. events]);
    }

    private const string Limits =
        """{"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":30.0,"window_minutes":43200,"resets_at":1794000000},"secondary":null}}}""";

    private const string Usage =
        """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":43089,"cached_input_tokens":41792,"output_tokens":66},"model_context_window":258400},"rate_limits":{"primary":null,"secondary":null}}}""";

    [Fact]
    public void Reads_the_terminal_sessions_last_usage_window_and_limits()
    {
        Rollout("terminal", "codex_cli_rs", @"X:\Project", Limits, Usage);

        var reading = CodexContextProbe.Read(_root, @"X:\Project\", DateTime.UtcNow.AddMinutes(-5));

        Assert.NotNull(reading);
        Assert.Equal(43089, reading.Value.ContextTokens);
        Assert.Equal(258400, reading.Value.ContextWindow);

        // A later event with empty windows does not blank the earlier real ones.
        Assert.Equal(0.30, reading.Value.Limits?.SevenDayUtilization);
        Assert.Equal(43200, reading.Value.Limits?.LongWindowMins);
    }

    [Fact]
    public void Skips_codales_own_chats_and_other_projects()
    {
        Rollout("chat", "codale", @"X:\Project", Usage);
        Rollout("elsewhere", "codex_cli_rs", @"X:\Other", Usage);

        Assert.Null(CodexContextProbe.Read(_root, @"X:\Project", DateTime.UtcNow.AddMinutes(-5)));
    }
}