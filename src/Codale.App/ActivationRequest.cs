using System.Web;

using Codale.Core.Projects;

using Microsoft.Windows.AppLifecycle;

using Windows.ApplicationModel.Activation;

namespace Codale.App;

/// <summary>What a launch is asking Codale to open.</summary>
/// <param name="ProjectPath">Normalised project root, or null to show the project picker.</param>
public sealed record ActivationRequest(string? ProjectPath)
{
    /// <summary>Instance key used when no project was named, so all launchers share one window.</summary>
    public const string LauncherKey = "codale-launcher";

    /// <summary>
    /// Every entry point funnels through here: the Explorer context menu, the picker and
    /// run.ps1 all launch <c>codale://open?path=...</c>, and a bare command line is
    /// supported so the app can be started from a terminal.
    /// </summary>
    public static ActivationRequest From(AppActivationArguments? activation, string[] commandLineArgs)
    {
        if (activation?.Kind == ExtendedActivationKind.Protocol &&
            activation.Data is IProtocolActivatedEventArgs protocol)
        {
            return new ActivationRequest(FromUri(protocol.Uri));
        }

        // Skip argv[0]; take the first argument that looks like a directory.
        foreach (var arg in commandLineArgs)
        {
            if (arg.StartsWith('-') || arg.StartsWith('/'))
            {
                continue;
            }

            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.Scheme == "codale")
            {
                return new ActivationRequest(FromUri(uri));
            }

            if (Directory.Exists(arg))
            {
                return new ActivationRequest(ProjectPaths.Normalize(arg));
            }
        }

        return new ActivationRequest(ProjectPath: null);
    }

    /// <summary>Parses <c>codale://open?path=&lt;urlencoded&gt;</c>.</summary>
    public static string? FromUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "codale", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = HttpUtility.ParseQueryString(uri.Query).Get("path");
        if (!IsLocalDirectory(path))
        {
            return null;
        }

        return ProjectPaths.Normalize(path!);
    }

    /// <summary>
    /// A codale:// link is untrusted input, and Directory.Exists on a UNC path makes Windows
    /// authenticate to the named host (an NTLM hash leak). Only drive-rooted paths pass: no
    /// UNC shares, no device paths (<c>\\?\</c>, <c>\\.\</c>), nothing relative.
    /// </summary>
    internal static bool IsLocalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        // Either slash direction starts a UNC or device path ("//server/share" works too).
        var isNetworkOrDevice = path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
        return !isNetworkOrDevice
            && Path.GetPathRoot(path) is { Length: >= 3 } root
            && char.IsAsciiLetter(root[0])
            && root[1] == ':'
            && Directory.Exists(path);
    }

    /// <summary>Builds the launch URI for a project. Used by the picker and the shell extension.</summary>
    public static Uri ToUri(string projectPath) =>
        new($"codale://open?path={Uri.EscapeDataString(ProjectPaths.Normalize(projectPath))}");

    /// <summary>
    /// One window per project: the key is a hash of the normalised path, because
    /// instance keys cannot hold arbitrary path characters.
    /// </summary>
    public string InstanceKey =>
        ProjectPath is null ? LauncherKey : ProjectPaths.InstanceKey(ProjectPath);
}
