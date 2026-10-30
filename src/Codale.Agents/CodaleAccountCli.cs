using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
    public static async Task<bool> IsSignedInAsync(string cliId, CancellationToken ct = default) =>
        (await GetAccountAsync(cliId, ct).ConfigureAwait(false)).SignedIn;

    /// <summary>
    /// Whether the account is signed in, plus its email when the CLI lets us know it: claude prints
    /// it in its JSON status, codex keeps it as a claim of the id token in <c>auth.json</c>.
    /// </summary>
    public static async Task<(bool SignedIn, string? Email)> GetAccountAsync(string cliId, CancellationToken ct = default)
    {
        var cli = Describe(cliId);
        if (!Directory.EnumerateFileSystemEntries(cli.Home).Any() || CliLocator.Find(cli.Executable) is not { } path)
        {
            return (false, null);
        }

        var (exitCode, output) = await RunAsync(path, cli.StatusArgs, cli.Variable, cli.Home, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return (false, null);
        }

        return (true, cliId == "codex" ? ReadCodexEmail(cli.Home) : ReadJsonString(output, "email"));
    }

    private static string? ReadCodexEmail(string home)
    {
        try
        {
            using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
            if (!auth.RootElement.TryGetProperty("tokens", out var tokens)
                || !tokens.TryGetProperty("id_token", out var idToken)
                || idToken.GetString()?.Split('.') is not [_, var payload, ..])
            {
                return null;
            }

            var base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            return ReadJsonString(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), "email");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static string? ReadJsonString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes the stored login; true when the CLI reports success.</summary>
    public static async Task<bool> SignOutAsync(string cliId, CancellationToken ct = default)
    {
        var cli = Describe(cliId);
        return CliLocator.Find(cli.Executable) is { } path
            && (await RunAsync(path, cli.LogoutArgs, cli.Variable, cli.Home, ct).ConfigureAwait(false)).ExitCode == 0;
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

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, string arguments, string variable, string home, CancellationToken ct)
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
                return (-1, string.Empty);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var kill = timeout.Token.Register(() => ProcessUtil.TryKillTree(process));

            // Both pipes are drained while waiting, so a chatty CLI never blocks on a full buffer.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
}
