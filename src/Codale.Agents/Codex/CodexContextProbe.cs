using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Agents.Codex;

/// <summary>What a Codex rollout last reported: the prompt's size, the model's window, and the account's windows.</summary>
public readonly record struct CodexContextReading(int ContextTokens, int? ContextWindow, RateLimitSnapshot? Limits);

/// <summary>
/// Reads how full the context is for a Codex CLI running in a terminal, from the rollout the CLI
/// keeps (<c>sessions/YYYY/MM/DD/rollout-*.jsonl</c>): its newest <c>token_count</c> event carries
/// the last request's usage, the model's window and the account's rate limits. Read-only, and only
/// as fresh as the CLI's last model call.
/// </summary>
public static class CodexContextProbe
{
    private const int TailBytes = 256 * 1024;

    /// <summary>
    /// The newest reading of a terminal session in <paramref name="projectPath"/> written since
    /// <paramref name="since"/>, or null. Codale's own chats share the folder only under Codale's
    /// account, but their rollouts (originator "codale") are skipped regardless.
    /// </summary>
    public static CodexContextReading? Read(string sessionsRoot, string projectPath, DateTime since)
    {
        try
        {
            if (!Directory.Exists(sessionsRoot))
            {
                return null;
            }

            var candidates = new DirectoryInfo(sessionsRoot)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= since)
                .OrderByDescending(f => f.LastWriteTimeUtc);

            foreach (var file in candidates)
            {
                if (IsTerminalSessionOf(file.FullName, projectPath))
                {
                    return ReadLatest(file.FullName);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsTerminalSessionOf(string path, string projectPath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        if (reader.ReadLine() is not { Length: > 0 } first)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(first);
            if (!doc.RootElement.TryGetProperty("payload", out var meta))
            {
                return false;
            }

            var originator = meta.TryGetProperty("originator", out var o) ? o.GetString() : null;
            var cwd = meta.TryGetProperty("cwd", out var c) ? c.GetString() : null;
            return !string.Equals(originator, "codale", StringComparison.OrdinalIgnoreCase) &&
                cwd is not null &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(cwd.Replace('/', '\\')),
                    Path.TrimEndingDirectorySeparator(projectPath.Replace('/', '\\')),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CodexContextReading? ReadLatest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        CodexContextReading? reading = null;
        RateLimitSnapshot? limits = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                // A sparse event (no plan behind it: BYOK) never blanks the last real windows.
                if (payload.TryGetProperty("rate_limits", out var rateLimits) && ParseRateLimits(rateLimits) is { } parsed)
                {
                    limits = parsed;
                }

                if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("last_token_usage", out var last) &&
                    Count(last, "input_tokens") is var input and > 0)
                {
                    // Codex's input already includes the cached part: it is the whole prompt.
                    int? window = info.TryGetProperty("model_context_window", out var w) && w.TryGetInt32(out var size) && size > 0 ? size : null;
                    reading = new CodexContextReading(input, window, null);
                }
            }
            catch (JsonException)
            {
                // The first line of a tail read is usually cut mid-record.
            }
        }

        return reading is { } found ? found with { Limits = limits } : limits is { } only ? new CodexContextReading(0, null, only) : null;
    }

    /// <summary>The rollout's snake_case windows: the five-hour one at or under ten hours, the long one beyond.</summary>
    internal static RateLimitSnapshot? ParseRateLimits(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var limits = new RateLimitSnapshot();
        var any = false;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("used_percent", out var used) || !used.TryGetDouble(out var percent))
            {
                continue;
            }

            var minutes = window.TryGetProperty("window_minutes", out var m) && m.TryGetInt64(out var mins) ? mins : 300;
            DateTimeOffset? resetsAt = window.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

            limits = minutes > 600
                ? limits with { SevenDayUtilization = percent / 100.0, SevenDayResetsAt = resetsAt, LongWindowMins = minutes }
                : limits with { FiveHourUtilization = percent / 100.0, FiveHourResetsAt = resetsAt };
            any = true;
        }

        return any ? limits : null;
    }

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
}
