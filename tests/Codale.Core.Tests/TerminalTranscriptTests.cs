using Codale.Core.Tasks;

namespace Codale.Core.Tests;

public sealed class TerminalTranscriptTests
{
    [Fact]
    public void Clear_drops_everything_held()
    {
        var transcript = new TerminalTranscript();
        transcript.Append("first run\r\n");

        transcript.Clear();

        Assert.Equal("", transcript.Read().Text);
    }

    [Fact]
    public void Clear_keeps_offsets_moving_forward_for_readers()
    {
        var transcript = new TerminalTranscript();
        transcript.Append("first run\r\n");
        var (_, offset) = transcript.Read();

        transcript.Clear();
        transcript.Append("second run\r\n");

        var (text, next) = transcript.Read(offset);
        Assert.Equal("second run\n", text);
        Assert.True(next > offset);
    }
}
