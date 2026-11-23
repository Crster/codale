using System.Runtime.InteropServices;
using System.Text;

namespace Codale.Core.Projects;

/// <summary>Locations for files owned by Codale.</summary>
public static class CodalePaths
{
    /// <summary>Set by the app so its child processes (MCP servers, Codale Browser) use its data root.</summary>
    public const string DataRootVariable = "CODALE_DATA_ROOT";

    /// <summary>
    /// The root for everything Codale stores: <c>%LOCALAPPDATA%\Codale</c>, or for a packaged
    /// app the physical folder its virtualised <c>%LOCALAPPDATA%\Codale</c> writes land in
    /// (<c>Packages\&lt;family&gt;\LocalCache\Local\Codale</c>). Using the physical path keeps
    /// one location that the app, Explorer and spawned processes all see the same way. Processes
    /// outside the package cannot tell the package's folder, so <see cref="DataRootVariable"/> wins.
    /// </summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>The root for per-project workspace databases.</summary>
    public static string WorkspaceDirectory => Path.Combine(DataRoot, "workspace");

    /// <summary>Hands <see cref="DataRoot"/> to every process started from this one.</summary>
    public static void ShareWithChildProcesses() =>
        Environment.SetEnvironmentVariable(DataRootVariable, DataRoot);

    /// <summary>The root for Codale's temporary files, created by callers as needed.</summary>
    public static string TempDirectory => Path.Combine(DataRoot, "temp");

    private static string ResolveDataRoot()
    {
        if (Environment.GetEnvironmentVariable(DataRootVariable) is { Length: > 0 } configured && Path.IsPathFullyQualified(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = PackageFamilyName() is { } family
            ? Path.Combine(localAppData, "Packages", family, "LocalCache", "Local")
            : localAppData;

        return Path.Combine(root, "Codale");
    }

    private static string? PackageFamilyName()
    {
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
            {
                return null;
            }

            var name = new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}
