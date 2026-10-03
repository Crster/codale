using System.Security.Cryptography;
using System.Text;

namespace Codale.Core.Projects;

/// <summary>
/// Path handling shared by instancing (one window per project) and by the
/// readers that locate a CLI's on-disk transcript for a project.
/// </summary>
public static class ProjectPaths
{
    /// <summary>
    /// Canonical form of a project root: absolute, link-resolved, no trailing separator.
    /// Two spellings of the same folder must normalise to the same string, otherwise
    /// the same project would open twice in two windows.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path.Trim().Trim('"'));

        // Resolve symlinks / junctions so a link and its target share one window.
        try
        {
            if (Directory.Exists(full) &&
                Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
            {
                full = target.FullName;
            }
        }
        catch (IOException)
        {
            // Unresolvable link: fall back to the literal path rather than failing to open.
        }

        if (full.Length > 3 && (full.EndsWith(Path.DirectorySeparatorChar) ||
                                full.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            full = full[..^1];
        }

        return full;
    }

    /// <summary>
    /// Stable key for <c>AppInstance.FindOrRegisterForKey</c>. Hashed rather than
    /// passed raw because instance keys have length and character constraints that
    /// a Windows path will not always satisfy.
    /// </summary>
    public static string InstanceKey(string path)
    {
        var normalized = Normalize(path).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Claude Code's on-disk transcript folder name for a working directory.
    /// </summary>
    /// <remarks>
    /// Verified empirically against claude 2.1.274: every character outside
    /// <c>[A-Za-z0-9-]</c> is replaced by <c>-</c> and case is preserved, so
    /// <c>X:\CrsterSite\Codale</c> becomes <c>X--CrsterSite-Codale</c> and
    /// <c>...\dot.test\my_app (v2)\work</c> becomes <c>...-dot-test-my-app--v2--work</c>.
    /// Note this mapping is lossy and therefore not reversible.
    /// </remarks>
    public static string ClaudeHistoryFolderName(string projectPath)
    {
        var normalized = Normalize(projectPath);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-');
        }

        return sb.ToString();
    }

    /// <summary>Full path to the folder holding this project's Claude session JSONL files.</summary>
    public static string ClaudeHistoryDirectory(string projectPath, string? claudeHome = null)
    {
        claudeHome ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");

        return Path.Combine(claudeHome, "projects", ClaudeHistoryFolderName(projectPath));
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or lies beneath it.
    /// Compares on a directory boundary, so <c>C:\proj-secrets</c> is not inside <c>C:\proj</c>.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short label for window titles and the project switcher.</summary>
    public static string DisplayName(string projectPath)
    {
        var normalized = Normalize(projectPath);
        var name = Path.GetFileName(normalized);
        return string.IsNullOrEmpty(name) ? normalized : name;
    }
}
