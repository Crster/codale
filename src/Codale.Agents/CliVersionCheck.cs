using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using Codale.Core.Processes;

namespace Codale.Agents;

/// <summary>
/// Which Claude CLI is installed and which is the newest published. Read-only: nothing
/// here changes the installation - updating is the user's click, run in a terminal tab.
/// </summary>
public static partial class CliVersionCheck
{
    /// <summary>The command that updates an installed CLI; null when there is no known way to.</summary>
    public static string? UpdateCommand(CliDescriptor cli) =>
        WingetPackage(cli) is { } wingetId ? $"winget upgrade --id {wingetId} --source winget --exact"
        : cli.Id == "claude" ? cli.Executable + " update"
        : NpmPackage(cli) is { } package ? $"npm i -g {package}@latest"
        : null;

    /// <summary>The npm package a CLI is published as; null when it is not an npm install.</summary>
    public static string? NpmPackage(CliDescriptor cli) =>
        cli.Id == "claude" ? "@anthropic-ai/claude-code"
        : cli.InstallCommand is { } install && NpmInstallPattern().Match(install) is { Success: true } match ? match.Groups[1].Value
        : null;

    /// <summary>
    /// The winget package id when the CLI on PATH was installed by winget (its portable
    /// link or package folder), else null. winget lags the npm registry, so a winget
    /// install is checked and updated against winget only.
    /// </summary>
    public static string? WingetPackage(CliDescriptor cli) =>
        WingetPackage(cli, CliLocator.Find(cli.Executable));

    internal static string? WingetPackage(CliDescriptor cli, string? path)
    {
        var id = cli.Id switch
        {
            "claude" => "Anthropic.ClaudeCode",
            "codex" => "OpenAI.Codex",
            _ => null,
        };

        return id is not null && path is not null &&
               path.Contains(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase)
            ? id
            : null;
    }

    [GeneratedRegex(@"^npm\s+(?:i|install)\s+-g\s+(\S+)$")]
    private static partial Regex NpmInstallPattern();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Runs <c>&lt;cli&gt; --version</c>; null when the CLI is missing or says nothing parseable.</summary>
    public static async Task<Version?> InstalledAsync(CliDescriptor cli, CancellationToken cancellation = default)
    {
        if (CliLocator.Find(cli.Executable) is not { } path)
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

            using var process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var kill = timeout.Token.Register(() => ProcessUtil.TryKillTree(process));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errorTask;
            return Parse(await outputTask);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The newest published release; null when the registry cannot be reached.</summary>
    public static async Task<Version?> LatestAsync(CliDescriptor cli, CancellationToken cancellation = default)
    {
        if (WingetPackage(cli) is { } wingetId)
        {
            return await LatestFromWingetAsync(wingetId, cancellation).ConfigureAwait(false);
        }

        if (NpmPackage(cli) is not { } package)
        {
            return null;
        }

        try
        {
            await using var stream = await Http.GetStreamAsync($"https://registry.npmjs.org/{package}/latest", cancellation);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            return document.RootElement.TryGetProperty("version", out var version) ? Parse(version.GetString()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The version the winget source offers (<c>winget show</c>); null when winget cannot say.</summary>
    private static async Task<Version?> LatestFromWingetAsync(string id, CancellationToken cancellation)
    {
        try
        {
            var startInfo = new ProcessStartInfo("winget")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "show", "--id", id, "--exact", "--source", "winget", "--disable-interactivity" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var kill = timeout.Token.Register(() => ProcessUtil.TryKillTree(process));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errorTask;

            var match = WingetVersionLine().Match(await outputTask);
            return match.Success ? Parse(match.Groups[1].Value) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^Version:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex WingetVersionLine();

    /// <summary>Pulls "2.1.5" out of "2.1.5 (Claude Code)" or a bare version string.</summary>
    public static Version? Parse(string? text) =>
        text is not null && VersionPattern().Match(text) is { Success: true } match && Version.TryParse(match.Value, out var version)
            ? version
            : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
