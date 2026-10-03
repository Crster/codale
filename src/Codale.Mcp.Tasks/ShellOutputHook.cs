using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Codale.Core.Agents;
using Codale.Core.Tasks;

namespace Codale.Mcp.Tasks;

/// <summary>
/// Claude Code <c>PostToolUse</c> hook on the shell tools: hands the agent a condensed copy
/// of a long command output (<see cref="ShellOutputCompressor"/>, then a model digest or a
/// head-and-tail cut when it is still long) and keeps the full output in a file it can
/// Read. The command has already run; only what the agent sees changes.
/// </summary>
public static class ShellOutputHook
{
    /// <summary>Past this, compressed output is digested (or cut) rather than passed on.</summary>
    public const int DigestChars = 12_000;

    /// <summary>Saved outputs (and the read guard's per-session state) older than this are cleared away.</summary>
    internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);

    /// <summary>The most a single saved output keeps; past it the middle is cut and marked.</summary>
    internal const int MaxSavedChars = 4 * 1024 * 1024;

    /// <summary>The sweep runs at most this often, so a busy session does not rescan the folder on every call.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);

    private const string SweepMarker = ".swept";

    /// <param name="hookInputJson">The CLI's hook payload.</param>
    /// <param name="assist">The app, which tallies savings and (with <paramref name="digest"/>) digests output that is still long; null cuts it instead.</param>
    /// <param name="outputRoot">Where full outputs are kept, one folder per session.</param>
    /// <param name="digest">Ask the app's background model for a digest before cutting.</param>
    /// <returns>The hook's stdout, or null to leave the result as it is.</returns>
    public static async Task<string?> EvaluateAsync(
        string hookInputJson, ITaskAssist? assist, string outputRoot, bool digest = true, CancellationToken ct = default)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(hookInputJson)?.AsObject() ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null; // never let a malformed payload get in the way of real work
        }

        if (root["tool_response"] is not JsonObject response)
        {
            return null;
        }

        var stdout = StringOf(response["stdout"]);
        var stderr = StringOf(response["stderr"]);
        if (stdout is null && stderr is null)
        {
            return null;
        }

        stdout ??= "";
        stderr ??= "";
        var command = StringOf(root["tool_input"]?["command"]) ?? "";
        var before = stdout.Length + stderr.Length;

        var compressed = ShellOutputCompressor.Compress(command, stdout, stderr);
        var (newOut, newErr) = (compressed.Stdout, compressed.Stderr);
        var how = "condensed";

        if (newOut.Length + newErr.Length > DigestChars)
        {
            var summary = assist is null || !digest ? null : await TryDigestAsync(assist, command, newOut, newErr, ct).ConfigureAwait(false);
            if (summary is { Length: > 0 })
            {
                (newOut, newErr) = (summary, "");
                how = "summarised (by Codale's background model)";
            }
            else
            {
                (newOut, newErr) = (ShellOutputCompressor.HeadTail(newOut), ShellOutputCompressor.HeadTail(newErr, 40, 40));
            }
        }

        var after = newOut.Length + newErr.Length;
        if (after >= before)
        {
            return null;
        }

        var saved = SaveFull(outputRoot, StringOf(root["session_id"]), StringOf(root["tool_use_id"]), stdout, stderr);
        var footer = $"\n[Codale {how} this output from {before:N0} to {after:N0} characters." +
                     (saved is null ? "]" : $" Full output: {saved} - Read it if you need more.]");
        if (newOut.Length > 0 || newErr.Length == 0)
        {
            newOut = newOut.TrimEnd() + footer;
        }
        else
        {
            newErr = newErr.TrimEnd() + footer;
        }

        // Every field the tool returned goes back: the CLI ignores a result that is not its own shape.
        var updated = response.DeepClone().AsObject();
        if (updated.ContainsKey("stdout") || newOut.Length > 0)
        {
            updated["stdout"] = newOut;
        }

        if (updated.ContainsKey("stderr") || newErr.Length > 0)
        {
            updated["stderr"] = newErr;
        }

        if (assist is TaskPipeClient pipe)
        {
            // The hook can await, so the tally does not block it on the sync wrapper.
            await pipe.RecordSavedAsync(before, newOut.Length + newErr.Length, ct).ConfigureAwait(false);
        }
        else
        {
            assist?.RecordSaved(before, newOut.Length + newErr.Length);
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["updatedToolOutput"] = updated,
            },
        }.ToJsonString();
    }

    private static async Task<string?> TryDigestAsync(ITaskAssist assist, string command, string stdout, string stderr, CancellationToken ct)
    {
        var both = stderr.Length == 0 ? stdout : $"{stdout}\n--- stderr ---\n{stderr}";
        try
        {
            return await assist.DigestAsync(command, both, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TaskServiceException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The full output on disk for the agent to Read later, or null when it could not be written.</summary>
    internal static string? SaveFull(string outputRoot, string? sessionId, string? toolUseId, string stdout, string stderr)
    {
        try
        {
            SweepStale(outputRoot, KeepFor);
            var dir = Path.Combine(outputRoot, SafeName(sessionId, "session"));
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, SafeName(toolUseId, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")) + ".log");
            var text = new StringBuilder(stdout);
            if (stderr.Length > 0)
            {
                text.Append("\n--- stderr ---\n").Append(stderr);
            }

            File.WriteAllText(path, Cap(text.ToString(), MaxSavedChars), new UTF8Encoding(false));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Keeps the head and the tail of <paramref name="text"/> (errors usually sit at the end) when it is longer than <paramref name="max"/>.</summary>
    internal static string Cap(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var half = max / 2;
        return text[..half] + $"\n[... Codale omitted {text.Length - 2 * half:N0} characters from the middle of this output ...]\n" + text[^half..];
    }

    /// <summary>
    /// Deletes files and folders under <paramref name="root"/> not touched for <paramref name="keepFor"/>.
    /// Throttled by a marker file, so calling it on every write costs one stat most of the time.
    /// </summary>
    internal static void SweepStale(string root, TimeSpan keepFor)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var marker = Path.Combine(root, SweepMarker);
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < SweepEvery)
            {
                return;
            }

            File.WriteAllBytes(marker, []);
            var cutoff = DateTime.UtcNow - keepFor;
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (entry.Name == SweepMarker || entry.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                try
                {
                    if (entry is DirectoryInfo dir)
                    {
                        dir.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only: a failed sweep must not cost the caller its result.
        }
    }

    internal static string SafeName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
        return safe.Length > 80 ? safe[..80] : safe;
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
