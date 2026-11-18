using System.Text;
using System.Text.Json;

namespace Codale.Agents.Claude;

public readonly record struct ClaudeContextReading(int Tokens, int ContextWindow);

/// <summary>Exchanges the active terminal's context counters through a per-launch status-line file.</summary>
public static class ClaudeContextProbe
{
    public static string StatusLineCommand(string path)
    {
        var destination = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var script = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{destination}}'))
            $context = ([Console]::In.ReadToEnd() | ConvertFrom-Json).context_window
            $reading = @{
                tokens = [int]$context.total_input_tokens + [int]$context.total_output_tokens
                window = [int]$context.context_window_size
            }
            [IO.File]::WriteAllText($path, ($reading | ConvertTo-Json -Compress))
            """;
        return "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static ClaudeContextReading? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var data = doc.RootElement;
            return data.Int("tokens") is >= 0 and int tokens && data.Int("window") is >= 0 and int window
                ? new ClaudeContextReading(tokens, window)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
