using System.Diagnostics;

using Codale.Core.Agents;
using Codale.Core.Processes;

namespace Codale.Agents;

/// <summary>
/// Asks a CLI whether Codale's own account (<see cref="CodaleAccount"/>) is signed in, and
/// signs it out. Both CLIs answer from their stored credentials with an exit code, so a
/// check never costs a model request; a token the server has since revoked only shows when a
/// turn is refused, which the chat already turns into a sign-in prompt.
/// </summary>
public static class CodaleAccountCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static (string Executable, string Variable, string Home, string StatusArgs, string LogoutArgs, string LoginArgs) Describe(string cliId) =>
        cliId == "codex"
            ? ("codex", CodaleAccount.CodexVariable, CodaleAccount.CodexHome, "login status", "logout", "login")
            : (CliLocator.Executable, CodaleAccount.ClaudeVariable, CodaleAccount.ClaudeHome, "auth status", "auth logout", "auth login");

    /// <summary>True when the account folder holds a login the CLI still accepts; false for an empty folder, a missing CLI or a signed-out one.</summary>
    public static async Task<bool> IsSignedInAsync(string cliId, CancellationToken ct = default)
    {
        var cli = Describe(cliId);
        if (!Directory.EnumerateFileSystemEntries(cli.Home).Any() || CliLocator.Find(cli.Executable) is not { } path)
        {
            return false;
        }

        return await RunAsync(path, cli.StatusArgs, cli.Variable, cli.Home, ct).ConfigureAwait(false) == 0;
    }

    /// <summary>Removes the stored login; true when the CLI reports success.</summary>
    public static async Task<bool> SignOutAsync(string cliId, CancellationToken ct = default)
    {
        var cli = Describe(cliId);
        return CliLocator.Find(cli.Executable) is { } path
            && await RunAsync(path, cli.LogoutArgs, cli.Variable, cli.Home, ct).ConfigureAwait(false) == 0;
    }

    /// <summary>
    /// A command line that signs Codale's account in, to type into any shell: it starts the CLI
    /// through cmd with the account folder set, so the terminal's own claude/codex stays untouched.
    /// </summary>
    public static string LoginCommand(string cliId)
    {
        var cli = Describe(cliId);
        return $"cmd /c \"set {cli.Variable}={cli.Home}&& {cli.Executable} {cli.LoginArgs}\"";
    }

    private static async Task<int> RunAsync(string executable, string arguments, string variable, string home, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[variable] = home;

        try
        {
            using var process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false);
            if (process is null)
            {
                return -1;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
