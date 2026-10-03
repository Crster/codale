using System.IO.Enumeration;

namespace Codale.Search;

/// <summary>
/// Files whose contents are credentials. What the search model reads is sent to whatever
/// endpoint backs it, and ripgrep's .gitignore handling is no protection outside a git
/// repository, so these are refused by name before any content is read.
/// </summary>
internal static class SensitivePaths
{
    private static readonly string[] FileNamePatterns =
    [
        ".env*", "*.pem", "*.key", "*.pfx", "*.p12", "*.keystore", "id_rsa*", "id_dsa*", "id_ecdsa*", "id_ed25519*",
        "credentials*", ".npmrc", ".netrc", ".pypirc", "secrets.*", "*.secrets",
    ];

    private static readonly string[] FolderNames = [".ssh", ".aws", ".gnupg"];

    public const string RefusalSuffix = " looks like a secrets file and is not available to search.";

    public static bool IsSensitive(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var folder in segments.AsSpan(0, segments.Length - 1))
        {
            if (FolderNames.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var name = segments[^1];
        foreach (var pattern in FileNamePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }
}
