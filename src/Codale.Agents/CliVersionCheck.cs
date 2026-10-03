using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Agents;

/// <summary>
/// Which Claude CLI is installed and which is the newest published. Read-only: nothing
/// here changes the installation - updating is the user's click, run in a terminal tab.
/// </summary>
public static partial class CliVersionCheck
{
    /// <summary>The command that updates an installed CLI, whichever way it was installed.</summary>
    public const string UpdateCommand = CliLocator.Executable + " update";

    private const string LatestUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Runs <c>claude --version</c>; null when the CLI is missing or says nothing parseable.</summary>
    public static async Task<Version?> InstalledAsync(CancellationToken cancellation = default)
    {
        if (CliLocator.Find(CliLocator.Executable) is not { } path)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(output);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The newest published release; null when the registry cannot be reached.</summary>
    public static async Task<Version?> LatestAsync(CancellationToken cancellation = default)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(LatestUrl, cancellation);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            return document.RootElement.TryGetProperty("version", out var version) ? Parse(version.GetString()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Pulls "2.1.5" out of "2.1.5 (Claude Code)" or a bare version string.</summary>
    public static Version? Parse(string? text) =>
        text is not null && VersionPattern().Match(text) is { Success: true } match && Version.TryParse(match.Value, out var version)
            ? version
            : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
