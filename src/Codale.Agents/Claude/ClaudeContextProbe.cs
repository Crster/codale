using System.Text.Json;

namespace Codale.Agents.Claude;

/// <summary>The tokens of a terminal session's last prompt, and the model that answered it.</summary>
public readonly record struct ClaudeContextReading(int Tokens, string? Model);

/// <summary>
/// Reads how full the context is for a Claude CLI running in a terminal, from the transcript the
/// CLI keeps: the newest assistant message's usage is the prompt it last sent. Read-only, and
/// only as fresh as the CLI's last turn.
/// </summary>
public static class ClaudeContextProbe
{
    private const int TailBytes = 256 * 1024;

    /// <summary>
    /// The last prompt of the session transcript touched since <paramref name="since"/>, or null.
    /// <paramref name="directory"/> is the project's transcript folder under the account the CLI runs
    /// on: a terminal CLI keeps the default one (~/.claude), not Codale's own.
    /// </summary>
    public static ClaudeContextReading? Read(string directory, DateTime since)
    {
        try
        {
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
            ClaudeContextReading? reading = null;
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
                        reading = new ClaudeContextReading(sum, message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null);
                    }
                }
                catch (JsonException)
                {
                    // The first line of a tail read is usually cut mid-record.
                }
            }

            return reading;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The window to measure against. The transcript never records it (a "[1m]" pick is logged
    /// under the bare model id), so <paramref name="known"/> is the window last seen for the
    /// model; without one it is a guess: 200K, or 1M once a session is already past 200K.
    /// </summary>
    public static int WindowFor(int tokens, int? known = null) =>
        Math.Max(known ?? 0, tokens > 200_000 ? 1_000_000 : 200_000);

    /// <summary>The model id windows are remembered under: "claude-sonnet-5-5[1m]" and "claude-sonnet-5-5" are one model.</summary>
    public static string ModelKey(string model)
    {
        var bracket = model.IndexOf('[');
        return (bracket > 0 ? model[..bracket] : model).Trim().ToLowerInvariant();
    }

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
}