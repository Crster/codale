using System.Text;

namespace Codale.Terminal.Tests;

/// <summary>
/// Exercises the real pseudoconsole. These actually start a shell, so they are the only
/// place the ConPTY handle-ordering rules get verified - get them wrong and the reader
/// hangs instead of failing, which is why each test has a hard timeout.
/// </summary>
public sealed class ConPtySmokeTests
{
    /// <summary>
    /// Collects everything the session emits. Sessions created on a thread with no
    /// SynchronizationContext deliver OutputReceived inline on the reader thread.
    /// </summary>
    private sealed class OutputCollector : IDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly TerminalSession _session = new();
        private readonly TaskCompletionSource _exited = new();

        public OutputCollector(string command)
        {
            _session.OutputReceived += (_, chunk) => _text.Append(chunk);
            _session.Exited += (_, _) => _exited.TrySetResult();
            _session.Start(Path.GetTempPath(), command);
        }

        public string Text => _text.ToString();

        public Task Exited => _exited.Task;

        public Task WriteAsync(string text) => _session.WriteAsync(text);

        public void Dispose() => _session.Dispose();
    }

    [Fact]
    public async Task A_command_runs_and_its_output_is_emitted()
    {
        using var collector = new OutputCollector("cmd.exe /c echo codale-conpty-works");

        await collector.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("codale-conpty-works", collector.Text);
    }

    [Fact]
    public async Task The_reader_sees_eof_when_the_child_exits()
    {
        // If the child's pipe ends are not closed after CreateProcess, this never
        // completes and the timeout fires.
        using var collector = new OutputCollector("cmd.exe /c exit");

        await collector.Exited.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Coloured_output_arrives_as_vt_sequences()
    {
        // ConPTY renders the VT itself; the emitted text carries whatever escape
        // sequences ConPTY chose to produce the colour.
        using var collector = new OutputCollector("cmd.exe /c echo \x1b[31mred-text\x1b[0m");

        await collector.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("red-text", collector.Text);
    }

    [Fact]
    public async Task An_interactive_shell_accepts_input_and_echoes_a_result()
    {
        using var collector = new OutputCollector("cmd.exe");

        // Give the shell a moment to print its banner and prompt before typing.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await collector.WriteAsync("echo codale-interactive\r\n");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (collector.Text.Contains("codale-interactive"))
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail("The shell never echoed the typed command:\n" + collector.Text);
    }
}
