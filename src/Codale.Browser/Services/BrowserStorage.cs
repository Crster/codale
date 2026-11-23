using System.Text.Json;

namespace Codale.Browser;

/// <summary>
/// What the app hands down when it starts the browser through the shell (which cannot pass env): a small file under the
/// user profile, outside any package's virtualised AppData. Env vars still win when present.
/// </summary>
internal static class LaunchHandoff
{
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codale", "browser-launch.json");

    private static readonly Dictionary<string, string> Values = Load();

    public static string? Get(string key) => Values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Where Codale Browser keeps its state: the data root the app passes down, or %LOCALAPPDATA%\Codale.</summary>
internal static class BrowserPaths
{
    public static string DataRoot { get; } = (Environment.GetEnvironmentVariable("CODALE_DATA_ROOT") is { Length: > 0 } env ? env : LaunchHandoff.Get("dataRoot")) is { } root && Path.IsPathFullyQualified(root)
        ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

    /// <summary>The WebView2 profile every tab shares: logins, cookies and page storage (WebView2 keeps these itself).</summary>
    public static string Profile => Environment.GetEnvironmentVariable("CODALE_WEBVIEW_PROFILE") is { Length: > 0 } profile
        ? profile
        : Path.Combine(DataRoot, "webview-browser-profile");
}
