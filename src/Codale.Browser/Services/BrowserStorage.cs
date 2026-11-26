using System.IO.Pipes;
using System.Text.Json;

namespace Codale.Browser;

/// <summary>Where Codale Browser keeps its state: the data root the app passes down, or %LOCALAPPDATA%\Codale.</summary>
internal static class BrowserPaths
{
    private static string? _launchDataRoot;

    public static void ReceiveLaunchDataRoot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            using var pipe = new NamedPipeClientStream(".", "Codale.Browser.Startup." + Environment.UserName,
                PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            if (reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() is { } line)
            {
                _launchDataRoot = JsonSerializer.Deserialize<string>(line);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            // A standalone launch uses the environment or the default folder.
        }
    }

    private static string DataRoot => (_launchDataRoot ?? Environment.GetEnvironmentVariable("CODALE_DATA_ROOT")) is { } root && Path.IsPathFullyQualified(root)
        ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

    /// <summary>The WebView2 user data folder containing the default profile and each project's profile.</summary>
    public static string UserDataFolder => Environment.GetEnvironmentVariable("CODALE_WEBVIEW_PROFILE") is { Length: > 0 } profile
        ? profile
        : Path.Combine(DataRoot, "webview-browser-profile");
}
