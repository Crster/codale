using System.Diagnostics;
using System.Text.Json;

namespace Codale.Browser;

/// <summary>Where Codale Browser keeps its state: the data root the app passes down, or %LOCALAPPDATA%\Codale.</summary>
internal static class BrowserPaths
{
    public static string DataRoot { get; } = Environment.GetEnvironmentVariable("CODALE_DATA_ROOT") is { Length: > 0 } root && Path.IsPathFullyQualified(root)
        ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

    /// <summary>The WebView2 profile every tab shares: logins, cookies and page storage.</summary>
    public static string Profile => Environment.GetEnvironmentVariable("CODALE_WEBVIEW_PROFILE") is { Length: > 0 } profile
        ? profile
        : Path.Combine(DataRoot, "webview-browser-profile");

    /// <summary>The browser's own stores: saved passwords, site permissions and passkey sites.</summary>
    public static string Folder { get; } = Ensure(Path.Combine(DataRoot, "browser"));

    private static string Ensure(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }
}

/// <summary>Small JSON documents written atomically (temp file, then move) so a crash never leaves half a store.</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : class, new()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T() : new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            return new T();
        }
    }

    /// <summary>Returns false when the file could not be written, so the caller can say so.</summary>
    public static bool Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }
}
