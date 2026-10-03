using System.Net.Http.Headers;
using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Agents.Claude;

/// <summary>
/// Reads the subscription usage the CLI signed in with, without a turn. The CLI only reports
/// it as a <c>rate_limit_event</c> while a turn runs, so the status bar would stay empty until
/// the first message; this asks the same usage endpoint the CLI's own <c>/usage</c> reads.
/// </summary>
public static class ClaudeUsageClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The current windows, or null when not signed in with a subscription or the call fails.</summary>
    public static async Task<RateLimitSnapshot?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var token = ReadAccessToken();
            if (token is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Maps the endpoint's reply (utilization 0-100, ISO reset times) onto a snapshot (0-1).</summary>
    internal static RateLimitSnapshot? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var (fiveHour, fiveHourReset) = Window(root, "five_hour");
        var (sevenDay, sevenDayReset) = Window(root, "seven_day");
        if (fiveHour is null && sevenDay is null)
        {
            return null;
        }

        return new RateLimitSnapshot
        {
            FiveHourUtilization = fiveHour,
            FiveHourResetsAt = fiveHourReset,
            SevenDayUtilization = sevenDay,
            SevenDayResetsAt = sevenDayReset,
        };
    }

    private static (double? Utilization, DateTimeOffset? ResetsAt) Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        double? utilization = window.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() / 100
            : null;
        DateTimeOffset? resetsAt = window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                                   DateTimeOffset.TryParse(r.GetString(), out var at)
            ? at
            : null;
        return (utilization, resetsAt);
    }

    private static string? ReadAccessToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
               oauth.ValueKind == JsonValueKind.Object &&
               oauth.TryGetProperty("accessToken", out var token) &&
               token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }
}
