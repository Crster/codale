using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codale.Mcp.Tasks;

/// <summary>
/// Claude Code <c>PreToolUse</c> hook on Read and on the shell tools: turns away a
/// whole-file read of a large file - a Read without a range, or a <c>cat</c> /
/// <c>type</c> / <c>Get-Content</c> of the file - and points the agent at a ranged read
/// (or the ask_files tool). A whole file read into context is paid for again on every
/// later call, and the agent usually needs one method of it. Asking for the same read
/// again goes through for a file of moderate size, so the agent is not stuck when it
/// really needs all of it; a very large file only ever comes in ranges.
/// </summary>
public static partial class ReadGuard
{
    public const int DefaultMaxLines = 400;

    /// <summary>Above this, even a repeated whole-file read is refused: ranges or ask_files only.</summary>
    public const int RepeatMaxLines = 1500;

    /// <summary>The prefix the app looks for to tell a redirect from a user's denial.</summary>
    public const string ReasonPrefix = "Codale:";

    private static readonly HashSet<string> NotText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".pdf", ".ipynb",
    };

    /// <param name="hookInputJson">The CLI's hook payload.</param>
    /// <param name="stateRoot">Where each session's refused paths are remembered.</param>
    /// <param name="maxLines">Files longer than this are refused once.</param>
    /// <param name="offerAskFiles">Whether the ask_files tool is there to suggest.</param>
    /// <returns>The hook's stdout, or null to let the read through.</returns>
    public static string? Evaluate(string hookInputJson, string stateRoot, int maxLines = DefaultMaxLines, bool offerAskFiles = false)
    {
        string? path;
        string? session;
        bool viaShell;
        try
        {
            var root = JsonNode.Parse(hookInputJson)?.AsObject();
            if (root?["tool_input"] is not JsonObject input)
            {
                return null;
            }

            session = root["session_id"]?.GetValue<string>();
            switch (root["tool_name"]?.GetValue<string>())
            {
                case "Read":
                    // A ranged read is exactly what the guard asks for.
                    if (input["offset"] is not null || input["limit"] is not null || input["pages"] is not null)
                    {
                        return null;
                    }

                    path = input["file_path"]?.GetValue<string>();
                    viaShell = false;
                    break;

                case "Bash" or "PowerShell":
                    path = WholeFileDump(input["command"]?.GetValue<string>() ?? "") is { } dumped
                        ? Path.Combine(root["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory, dumped)
                        : null;
                    viaShell = true;
                    break;

                default:
                    return null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path) || NotText.Contains(Path.GetExtension(path)))
        {
            return null;
        }

        var lines = CountLines(path, stopAfter: 1_000_000);
        if (lines <= maxLines)
        {
            return null;
        }

        // A second ask is deliberate: it goes through, unless the file is too big to ever take whole.
        var first = RememberRefusal(stateRoot, session, path);
        if (!first && lines <= RepeatMaxLines)
        {
            return null;
        }

        var name = Path.GetFileName(path);
        var how = viaShell
            ? $"{ReasonPrefix} {name} has {lines:N0} lines, so printing the whole file through the shell was skipped to save context. "
            : $"{ReasonPrefix} {name} has {lines:N0} lines, so this whole-file Read was skipped to save context. ";
        var instead = "Grep for the symbol you need and Read just that range with offset/limit" +
                      (offerAskFiles ? ", or ask mcp__codale-tasks__ask_files a question about the file if you only need to understand it" : "") +
                      ".";
        var escape = lines > RepeatMaxLines
            ? $" Files over {RepeatMaxLines:N0} lines are only read in ranges (at most {RepeatMaxLines:N0} lines at a time)."
            : viaShell
                ? " If you really need all of it, use Read on the whole file."
                : " If you really need the whole file, repeat this exact Read and it will go through.";

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = how + instead + escape,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// The file a shell command prints whole - <c>cat [-n] f</c>, <c>type f</c>,
    /// <c>Get-Content f</c>, <c>gc f</c> - or null for anything else: a pipe, a range
    /// (<c>-TotalCount</c>, <c>-Tail</c>, <c>sed -n</c>) or several files.
    /// </summary>
    internal static string? WholeFileDump(string command)
    {
        var match = DumpPattern().Match(command.Trim());
        if (!match.Success)
        {
            return null;
        }

        var file = match.Groups["file"].Value.Trim('"', '\'');
        if (file.Length == 0 || file.StartsWith('-') || file.StartsWith('$'))
        {
            return null;
        }

        // "cd somewhere && cat file": the file is relative to that folder.
        var dir = match.Groups["dir"].Value.Trim('"', '\'');
        return dir.Length > 0 && !Path.IsPathRooted(file) ? Path.Combine(dir, file) : file;
    }

    [GeneratedRegex(
        @"^(?:cd\s+(?<dir>""[^""]*""|'[^']*'|\S+)\s*(?:&&|;)\s*)?(?:cat(?:\s+-[nAbsv]+)*|type|Get-Content(?:\s+-(?:Path|LiteralPath|Raw|Encoding\s+\S+))*|gc(?:\s+-Raw)?)\s+(?<file>""[^""]+""|'[^']+'|[^\s|;&<>]+)(?:\s+-(?:Raw|Encoding\s+\S+))*\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DumpPattern();

    /// <summary>The file's line count, or 0 when it cannot be read (the CLI then reports that itself).</summary>
    internal static int CountLines(string path, int stopAfter)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            var buffer = new byte[64 * 1024];
            var lines = 0;
            var last = (byte)'\n';
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                lines += buffer.AsSpan(0, read).Count((byte)'\n');
                last = buffer[read - 1];
                if (lines > stopAfter)
                {
                    break;
                }
            }

            return stream.Length == 0 ? 0 : last == '\n' ? lines : lines + 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>True the first time this session asks for this path (and records it); false after.</summary>
    internal static bool RememberRefusal(string stateRoot, string? session, string path)
    {
        try
        {
            var key = Path.GetFullPath(path).ToLowerInvariant();
            var file = Path.Combine(stateRoot, ShellOutputHook.SafeName(session, "session") + ".txt");
            if (File.Exists(file) && File.ReadLines(file).Contains(key))
            {
                return false;
            }

            ShellOutputHook.SweepStale(stateRoot, ShellOutputHook.KeepFor);
            Directory.CreateDirectory(stateRoot);
            File.AppendAllLines(file, [key]);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Without a memory the guard could refuse forever: let the read through.
            return false;
        }
    }
}
