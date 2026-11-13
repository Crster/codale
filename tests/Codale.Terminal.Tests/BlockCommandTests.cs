using System.Text;

namespace Codale.Terminal.Tests;

/// <summary>
/// A notebook terminal runs every command block in a process of its own; what makes it feel like
/// one shell is the wrapper saving the folder and environment a block ended with, and the next
/// block starting from them. These pin the wrapper and the state round trip, against real shells.
/// </summary>
public sealed class BlockCommandTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "codale-block-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PowerShell_blocks_encode_a_wrapper_that_carries_the_command()
    {
        var launch = BlockCommand.Build(@"C:\Program Files\PowerShell\7\pwsh.exe", "echo 'it''s'", _stateDirectory);

        Assert.StartsWith("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoProfile -EncodedCommand ", launch.CommandLine);
        var encoded = launch.CommandLine[(launch.CommandLine.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        // Single quotes are doubled inside the PowerShell literal, and the state file is named in it.
        Assert.Contains("'echo ''it''''s'''", script);
        Assert.Contains(launch.StateFile, script);
        Assert.Null(launch.ScriptFile);
    }

    [Fact]
    public void Loading_the_profile_drops_NoProfile()
    {
        var launch = BlockCommand.Build("pwsh.exe", "dir", _stateDirectory, loadProfile: true);

        Assert.DoesNotContain("-NoProfile", launch.CommandLine);
    }

    [Fact]
    public void Cmd_blocks_run_a_script_file()
    {
        var launch = BlockCommand.Build(@"C:\Windows\system32\cmd.exe", "echo hi", _stateDirectory);

        Assert.NotNull(launch.ScriptFile);
        Assert.Equal($"\"C:\\Windows\\system32\\cmd.exe\" /d /c \"\"{launch.ScriptFile}\"\"", launch.CommandLine);
        var script = File.ReadAllText(launch.ScriptFile!);
        Assert.Contains("echo hi", script);
        Assert.Contains(launch.StateFile, script);

        launch.Cleanup();
        Assert.False(File.Exists(launch.ScriptFile));
    }

    [Fact]
    public void PowerShell_state_parses_folder_and_environment()
    {
        var state = ShellState.Parse("""{"cwd":"C:\\work","env":{"Path":"C:\\bin","FOO":"1"},"exit":0}""");

        Assert.NotNull(state);
        Assert.Equal(@"C:\work", state!.Cwd);
        Assert.Equal("1", state.Environment["foo"]);
        Assert.Equal(@"C:\bin", state.Environment["PATH"]);
    }

    [Fact]
    public void Cmd_state_parses_folder_and_set_output()
    {
        var state = ShellState.Parse("C:\\work\r\n=C:=C:\\work\r\nFOO=a=b\r\n__codale_exit=0\r\nPath=C:\\bin\r\n");

        Assert.NotNull(state);
        Assert.Equal(@"C:\work", state!.Cwd);
        Assert.Equal("a=b", state.Environment["FOO"]);
        Assert.False(state.Environment.ContainsKey(BlockCommand.CmdExitVariable));
        Assert.Equal(2, state.Environment.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"env\":{}}")]
    public void Unusable_state_is_null(string text) => Assert.Null(ShellState.Parse(text));

    // ---- Real shells -----------------------------------------------------------------------

    private async Task<(int ExitCode, string Output, ShellState? State)> RunAsync(string shell, string command, ShellState start)
    {
        var launch = BlockCommand.Build(shell, command, _stateDirectory);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>();
        using var session = new TerminalSession();
        session.OutputReceived += (_, chunk) => { lock (output) { output.Append(chunk); } };
        session.Exited += (_, code) => exited.TrySetResult(code);
        session.Start(start.Cwd, launch.CommandLine, 120, 30, start.Environment);

        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var state = ShellState.Read(launch.StateFile);
        launch.Cleanup();
        lock (output)
        {
            return (exitCode, output.ToString(), state);
        }
    }

    private static string? PowerShell() =>
        new[] { "pwsh.exe", "powershell.exe" }
            .SelectMany(exe => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(dir => dir.Length > 0)
                .Select(dir => Path.Combine(dir, exe)))
            .FirstOrDefault(File.Exists);

    [Fact]
    public async Task A_PowerShell_block_carries_its_folder_and_environment_to_the_next()
    {
        var shell = PowerShell();
        if (shell is null)
        {
            return;
        }

        var start = ShellState.Initial(Path.Combine(Path.GetTempPath()));
        var parent = Directory.GetParent(start.Cwd.TrimEnd('\\'))!.FullName;

        var (code, _, state) = await RunAsync(shell, "Set-Location ..; $env:CODALE_BLOCK_TEST = 'carried'", start);

        Assert.Equal(0, code);
        Assert.NotNull(state);
        Assert.Equal(parent.TrimEnd('\\'), state!.Cwd.TrimEnd('\\'), ignoreCase: true);
        Assert.Equal("carried", state.Environment["CODALE_BLOCK_TEST"]);

        var (nextCode, output, _) = await RunAsync(shell, "Write-Output \"value=$env:CODALE_BLOCK_TEST\"", state);
        Assert.Equal(0, nextCode);
        Assert.Contains("value=carried", output);
    }

    [Fact]
    public async Task A_PowerShell_block_prints_formatted_objects_before_it_exits()
    {
        var shell = PowerShell();
        if (shell is null)
        {
            return;
        }

        // Objects are formatted in batches; the wrapper's exit must not drop a table still pending.
        var marker = "codale-" + Guid.NewGuid().ToString("N")[..8];
        var (code, output, _) = await RunAsync(shell, $"[pscustomobject]@{{ Name = '{marker}' }}", ShellState.Initial(Path.GetTempPath()));

        Assert.Equal(0, code);
        Assert.Contains(marker, output);
    }

    [Fact]
    public async Task A_PowerShell_block_reports_failures()
    {
        var shell = PowerShell();
        if (shell is null)
        {
            return;
        }

        var start = ShellState.Initial(Path.GetTempPath());

        var (native, _, nativeState) = await RunAsync(shell, "cmd /c exit 3", start);
        Assert.Equal(3, native);
        Assert.NotNull(nativeState);

        var (syntax, _, syntaxState) = await RunAsync(shell, "if (", start);
        Assert.Equal(1, syntax);
        Assert.NotNull(syntaxState);

        // A command that exits itself writes no state; the caller keeps the previous one.
        var (exited, _, exitState) = await RunAsync(shell, "exit 5", start);
        Assert.Equal(5, exited);
        Assert.Null(exitState);
    }

    [Fact]
    public async Task A_cmd_block_carries_its_folder_and_environment()
    {
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = ShellState.Initial(Path.GetTempPath());
        var parent = Directory.GetParent(start.Cwd.TrimEnd('\\'))!.FullName;

        var (code, _, state) = await RunAsync(shell, "cd .. & set CODALE_BLOCK_TEST=cmd", start);

        Assert.Equal(0, code);
        Assert.NotNull(state);
        Assert.Equal(parent.TrimEnd('\\'), state!.Cwd.TrimEnd('\\'), ignoreCase: true);
        Assert.Equal("cmd", state.Environment["CODALE_BLOCK_TEST"]);

        var (failed, _, _) = await RunAsync(shell, "cmd /c exit 4", start);
        Assert.Equal(4, failed);
    }
}
