using System.Text.Json;

namespace Codale.Agents.Agy;

/// <summary>The tokens of an Antigravity CLI terminal session's last response, and the model that answered it.</summary>
public readonly record struct AgyContextReading(int ContextTokens, int ContextWindow, string? Model);

/// <summary>
/// Reads how full the context is for an Antigravity CLI running in a terminal, from the transcript the
/// CLI keeps (<c>brain/&lt;conversationId&gt;/.system_generated/logs/transcript.jsonl</c>). Read-only, and
/// only as fresh as the CLI's last model turn.
/// </summary>
public static class AgyContextProbe
{
    private const int TailBytes = 256 * 1024;
    public const int DefaultWindow = 1_000_000;

    /// <summary>
    /// Reads the newest context metrics from the transcript of an Antigravity session.
    /// <paramref name="historySourcePath"/> is the <c>history.jsonl</c> file path.
    /// </summary>
    public static AgyContextReading? Read(string historySourcePath, string projectPath, DateTime since)
    {
        try
        {
            var agyHome = Path.GetDirectoryName(historySourcePath);
            if (string.IsNullOrWhiteSpace(agyHome) || !Directory.Exists(agyHome))
            {
                agyHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
            }

            var model = ReadModel(agyHome);
            var window = WindowFor(model);

            // First try resolving conversation ID from history.jsonl for this project.
            string? transcriptFile = null;
            if (File.Exists(historySourcePath))
            {
                var convId = FindConversationId(historySourcePath, projectPath);
                if (convId is not null)
                {
                    var file = Path.Combine(agyHome, "brain", convId, ".system_generated", "logs", "transcript.jsonl");
                    if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= since)
                    {
                        transcriptFile = file;
                    }
                }
            }

            // Fallback: newest transcript.jsonl under brain/ modified since 'since'.
            if (transcriptFile is null)
            {
                var brainDir = Path.Combine(agyHome, "brain");
                if (!Directory.Exists(brainDir))
                {
                    return null;
                }

                transcriptFile = Directory.EnumerateFiles(brainDir, "transcript.jsonl", SearchOption.AllDirectories)
                    .Where(f => File.GetLastWriteTimeUtc(f) >= since)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
            }

            if (transcriptFile is null)
            {
                return null;
            }

            return ReadLatestTokens(transcriptFile, window, model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AgyContextReading? ReadLatestTokens(string path, int window, string? model)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        AgyContextReading? reading = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] != '{' || !line.Contains("\"input_tokens\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("input_tokens", out var inTok) ||
                    !root.TryGetProperty("type", out var type) ||
                    type.GetString() != "PLANNER_RESPONSE")
                {
                    continue;
                }

                var input = inTok.GetInt32();
                var cache = root.TryGetProperty("cache_read_tokens", out var cTok) ? cTok.GetInt32() : 0;
                var total = input + cache;
                if (total > 0)
                {
                    reading = new AgyContextReading(total, Math.Max(window, total), model);
                }
            }
            catch (JsonException)
            {
                // Tail read may start mid-line.
            }
        }

        return reading;
    }

    private static string? FindConversationId(string historyPath, string projectPath)
    {
        var normProject = Normalize(projectPath);
        using var stream = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? found = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("conversationId", out var idProp) &&
                    root.TryGetProperty("workspace", out var wsProp))
                {
                    var ws = wsProp.GetString();
                    if (ws is not null && (Normalize(ws) == normProject || string.IsNullOrEmpty(normProject)))
                    {
                        found = idProp.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return found;
    }

    private static string? ReadModel(string agyHome)
    {
        try
        {
            var settings = Path.Combine(agyHome, "settings.json");
            if (!File.Exists(settings))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static int WindowFor(string? model)
    {
        if (model is null) return DefaultWindow;
        if (model.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return 2_000_000;
        return 1_000_000;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();
}
