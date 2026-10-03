using System.Text;

namespace Codale.Terminal.Tests;

/// <summary>
/// The wrapping that makes a user command runnable in a terminal tab: it must go
/// through a shell (CreateProcess cannot run the .cmd shims npm installs as), and the
/// shell must stay open so a one-shot command's output is still there to read.
/// </summary>
public sealed class CommandShellTests
{
    private const string EncodedMarker = "-NoExit -EncodedCommand ";

    [Fact]
    public void PowerShell_shells_encode_the_command_and_stay_open()
    {
        var command = "npm run dev --config \"my config.json\"";
        var line = TerminalSession.WrapCommand(@"C:\Program Files\PowerShell\7\pwsh.exe", command);

        // The shell path is quoted (Program Files), and -NoExit keeps the tab's shell
        // alive after the command ends.
        Assert.StartsWith("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoExit -EncodedCommand ", line);

        // EncodedCommand carries the command as UTF-16 base64, so no quoting of the
        // command itself was needed - it must decode back exactly.
        var encoded = line[(line.IndexOf(EncodedMarker) + EncodedMarker.Length)..];
        Assert.Equal(command, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void Cmd_shells_keep_the_command_inline()
    {
        var line = TerminalSession.WrapCommand(@"C:\Windows\system32\cmd.exe", "echo hi");

        Assert.Equal("\"C:\\Windows\\system32\\cmd.exe\" /k \"echo hi\"", line);
    }

    [Fact]
    public void BuildCommandShell_resolves_a_shell_and_wraps_the_command()
    {
        var line = TerminalSession.BuildCommandShell("dotnet run");

        // Every Windows machine resolves a PowerShell for the default shell, so the
        // encoded form is the one in play; the command rides inside it.
        Assert.Contains(EncodedMarker, line);
    }
}
