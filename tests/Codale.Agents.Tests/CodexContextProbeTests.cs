using Codale.Agents.Codex;

namespace Codale.Agents.Tests;

public sealed class CodexContextProbeTests : IDisposable
{
    private const string Session = "019cbcec-4242-7834-b378-fb8ca3a63248";
    private const string Other = "019cbcec-4242-7834-b378-fb8cb3b11111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-probe-" + Guid.NewGuid().ToString("N"));

    public CodexContextProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Rollout(string id, params string[] events)
    {
        var day = Path.Combine(_root, "2026", "10", "14");
        Directory.CreateDirectory(day);
        var meta = $$$"""{"type":"session_meta","payload":{"id":"{{{id}}}","cwd":"X:/Project"}}""";
        File.WriteAllLines(Path.Combine(day, $"rollout-2026-10-14T00-00-00-{id}.jsonl"), [meta, .. events]);
    }

    private const string Limits =
        """{"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":30.0,"window_minutes":43200,"resets_at":1794000000},"secondary":null}}}""";

    private const string Usage =
        """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":43089,"cached_input_tokens":41792,"output_tokens":66},"model_context_window":475000},"rate_limits":{"primary":null,"secondary":null}}}""";

    [Fact]
    public void Selects_the_active_thread_and_preserves_rate_limits_from_sparse_events()
    {
        Rollout(Session, Limits, Usage);
        Rollout(Other, Usage.Replace("475000", "258400"));

        var abbreviatedTitle = Session[..29] + "...";
        var id = Assert.IsType<string>(CodexContextProbe.SessionIdFromTitle(abbreviatedTitle));
        var reading = Assert.IsType<CodexContextReading>(CodexContextProbe.Read(_root, id));

        Assert.Equal(43089, reading.ContextTokens);
        Assert.Equal(475000, reading.ContextWindow);
        Assert.Equal(0.30, reading.Limits?.SevenDayUtilization);
        Assert.Equal(43200, reading.Limits?.LongWindowMins);
        Assert.Equal(258400, CodexContextProbe.Read(_root, Other)?.ContextWindow);
    }

    [Fact]
    public void Ambiguous_or_missing_thread_ids_do_not_select_another_sessions_rollout()
    {
        Rollout(Session, Usage);
        Rollout("019cbcec-4242-7834-b378-fb8ca3b11111", Usage);
        Assert.Null(CodexContextProbe.Read(_root, Session[..29]));
        Assert.Null(CodexContextProbe.Read(_root, Guid.NewGuid().ToString("D")));
        Assert.Null(CodexContextProbe.SessionIdFromTitle("codex"));
    }

    [Fact]
    public void Usage_without_a_window_does_not_reuse_the_earlier_window()
    {
        Rollout(Session, Usage, Usage.Replace("475000", "null"));

        var reading = Assert.IsType<CodexContextReading>(CodexContextProbe.Read(_root, Session));

        Assert.Equal(43089, reading.ContextTokens);
        Assert.Null(reading.ContextWindow);
    }
}
