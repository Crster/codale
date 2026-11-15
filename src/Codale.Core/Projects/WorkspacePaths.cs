namespace Codale.Core.Projects;

/// <summary>
/// The path rules the app applies to files in a project or repository: resolving a relative path under a root,
/// containment, and the path relative to a root. Containment always requires a separator boundary, so a sibling
/// folder that shares a name prefix (<c>app2</c> beside <c>app</c>) is never mistaken for a file inside it.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>
    /// The absolute path for <paramref name="path"/>: rooted paths are normalised as they are, relative ones are resolved
    /// under <paramref name="root"/>. Null when the path is not valid.
    /// </summary>
    public static string? ResolveUnder(string root, string path)
    {
        try
        {
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies beneath <paramref name="root"/>. The root itself counts only when
    /// <paramref name="includeRoot"/> is true. Comparison ignores case, as Windows paths do.
    /// </summary>
    public static bool IsWithin(string path, string root, bool includeRoot = true)
    {
        try
        {
            var full = Trim(Path.GetFullPath(path));
            var rootFull = Trim(Path.GetFullPath(root));
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return includeRoot;
            }

            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path of <paramref name="path"/> relative to <paramref name="root"/>, with forward slashes; null when it lies
    /// outside the root.
    /// </summary>
    public static string? RelativeWithin(string root, string path)
    {
        if (!IsWithin(path, root))
        {
            return null;
        }

        return Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar);
}
