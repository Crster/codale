using System.Diagnostics;
using System.Text;

using Codale.Core.Processes;

namespace Codale.Git;

/// <summary>
/// The outcome of one git invocation. <see cref="Truncated"/> means stdout hit the size
/// cap and was cut at the last whole record; the process itself still ran to completion.
/// </summary>
internal sealed record GitResult(bool Success, string StandardOutput, string StandardError, bool Truncated = false);

/// <summary>
/// The one place git is spawned, shared by <see cref="GitRepository"/> and
/// <see cref="GitWorktrees"/> so timeout, prompt and output-size handling cannot drift.
/// </summary>
internal static class GitProcess
{
    /// <summary>Polls and reads: a wedged git (locked index, network drive) must not hold the panel forever.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Writes run hooks and talk to remotes; killing a slow push or commit midway is worse than waiting.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound on captured stdout, so a runaway diff or log cannot exhaust memory.</summary>
    public const int MaxOutputChars = 16 * 1024 * 1024;

    private const int MaxErrorChars = 256 * 1024;

    /// <summary>
    /// Refs and branch names come from the UI or a model; one starting with '-' would be
    /// read by git as an option (<c>--output=...</c> writes a file).
    /// </summary>
    public static bool IsSafeRef(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name[0] != '-' && !name.Any(char.IsControl);

    /// <param name="readOnly">
    /// Poll-style commands set <c>GIT_OPTIONAL_LOCKS=0</c> so a refresh never takes the
    /// index lock out from under a commit or an agent's own git call.
    /// </param>
    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool readOnly,
        TimeSpan timeout,
        CancellationToken ct,
        int maxOutputChars = MaxOutputChars)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // A remote asking for credentials must fail fast, not hang the panel waiting
        // for a prompt no one can see.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        if (readOnly)
        {
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        // Verbatim paths: without this git octal-escapes non-ASCII names in diff headers.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git is not installed or not on PATH.");

        // A timeout kills the whole tree and surfaces as a cancelled (failed) call.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdout = ReadCappedAsync(process.StandardOutput, maxOutputChars, cts.Token);
        var stderr = ReadCappedAsync(process.StandardError, MaxErrorChars, cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessUtil.TryKillTree(process);
            throw;
        }

        var (output, truncated) = await stdout.ConfigureAwait(false);
        var (error, _) = await stderr.ConfigureAwait(false);

        return new GitResult(process.ExitCode == 0, output, error, truncated);
    }

    /// <summary>
    /// Reads to the end but keeps at most <paramref name="maxChars"/>; the excess is read
    /// and dropped so the child never blocks on a full pipe.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        TextReader reader, int maxChars, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (truncated)
            {
                continue;
            }

            var room = maxChars - builder.Length;
            if (read > room)
            {
                builder.Append(buffer, 0, room);
                truncated = true;
            }
            else
            {
                builder.Append(buffer, 0, read);
            }
        }

        if (!truncated)
        {
            return (builder.ToString(), false);
        }

        // Cut back to the last whole line / NUL record so a parser never sees half of one.
        var text = builder.ToString();
        var cut = text.AsSpan().LastIndexOfAny('\n', '\0');
        return (cut > 0 ? text[..(cut + 1)] : text, true);
    }
}
