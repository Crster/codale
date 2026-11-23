namespace Codale.Core.Projects;

/// <summary>Locations for files owned by Codale.</summary>
public static class CodalePaths
{
    /// <summary>The root for Codale's temporary files, created by callers as needed.</summary>
    public static string TempDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "temp");
}
