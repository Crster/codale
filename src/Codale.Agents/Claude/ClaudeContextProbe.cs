using System.Text.Json;

using Codale.Core.Projects;

namespace Codale.Agents.Claude;

/// <summary>
/// Reads how full the context is for a Claude CLI running in a terminal, from the transcript the
/// CLI keeps: the newest assistant message's usage is the prompt it last sent. Read-only, and
/// only as fresh as the CLI's last turn.
/// </summary>
public static class ClaudeContextProbe
{
    private const int TailBytes = 256 * 1024;

    /// <summary>Tokens in the last prompt of the session transcript touched since <paramref name="since"/>, or null.</summary>
    public static int? ReadTokens(string projectPath, DateTime since)
    {
        try
        {
            var directory = ProjectPaths.ClaudeHistoryDirectory(projectPath);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var newest = new DirectoryInfo(directory).EnumerateFiles("*.jsonl")
                .Where(f => f.LastWriteTimeUtc >= since)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            int? tokens = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] != '{' || !line.Contains("\"usage\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                        !root.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("usage", out var usage))
                    {
                        continue;
                    }

                    var sum = Count(usage, "input_tokens") + Count(usage, "cache_read_input_tokens") + Count(usage, "cache_creation_input_tokens");
                    if (sum > 0)
                    {
                        tokens = sum;
                    }
                }
                catch (JsonException)
                {
                    // The first line of a tail read is usually cut mid-record.
                }
            }

            return tokens;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The window to measure against: 200K, or 1M once a session is already past 200K.</summary>
    public static int WindowFor(int tokens) => tokens > 200_000 ? 1_000_000 : 200_000;

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
}
