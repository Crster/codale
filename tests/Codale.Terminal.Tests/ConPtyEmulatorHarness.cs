using Xunit.Abstractions;

namespace Codale.Terminal.Tests;

/// <summary>
/// Headless check of the full pipeline: a real ConPTY shell feeding the real
/// TerminalEmulator, then the buffer dumped as text. This is the diagnostic for
/// "the terminal renders blank": it splits the problem between emulation and drawing.
/// </summary>
public class ConPtyEmulatorHarness
{
    private readonly ITestOutputHelper _output;

    public ConPtyEmulatorHarness(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Real_shell_output_populates_the_buffer()
    {
        var emulator = new TerminalEmulator(80, 25);
        var chunks = 0;
        var done = new TaskCompletionSource();

        var session = new TerminalSession();
        session.OutputReceived += (_, text) =>
        {
            Interlocked.Increment(ref chunks);
            emulator.Feed(text);
        };
        session.Exited += (_, _) => done.TrySetResult();

        session.Start(".", TerminalSession.ResolveDefaultShell());
        await session.WriteAsync("exit\r");

        // The shell gets a moment to flush its banner and exit.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(300);

        _output.WriteLine($"chunks received: {chunks}, scrollback: {emulator.ScrollbackCount}");
        for (var row = 0; row < emulator.Rows; row++)
        {
            var line = new string(emulator.GetScreenRow(row).ToArray().Select(c => c.Code).ToArray());
            _output.WriteLine($"{row,2}| {line.Replace("\0", "~")}");
        }

        var screenText = string.Join("\n", Enumerable.Range(0, emulator.Rows)
            .Select(r => new string(emulator.GetScreenRow(r).ToArray().Select(c => c.Code).ToArray())));

        Assert.True(chunks > 0, "no output reached the emulator");
        Assert.Contains("exit", screenText, StringComparison.OrdinalIgnoreCase);
    }
}
