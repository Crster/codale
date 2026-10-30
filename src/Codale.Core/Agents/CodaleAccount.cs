using System.Runtime.InteropServices;
using System.Text;

namespace Codale.Core.Agents;

/// <summary>
/// Codale signs in to Claude and Codex on its own: every CLI process it starts for a chat,
/// the helper or the model catalogue keeps its login, settings and session history in
/// folders under Codale's data directory instead of the CLI's default (<c>~/.claude</c>,
/// <c>~/.codex</c>). A CLI run directly in a terminal keeps the default account, so a
/// work login in Codale and a personal one in the terminal never meet.
/// </summary>
public static class CodaleAccount
{
    private static readonly Lazy<string> RootPath = new(ResolveRoot);

    private static string Root => RootPath.Value;

    /// <summary>The folder passed to the Claude CLI as <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static string ClaudeHome => Ensure(Path.Combine(Root, "claude"));

    /// <summary>The folder passed to the Codex CLI as <c>CODEX_HOME</c>; codex refuses to start when it is missing.</summary>
    public static string CodexHome => Ensure(Path.Combine(Root, "codex"));

    /// <summary>
    /// The default account's folder, which a Claude CLI run in a terminal uses: wherever the user
    /// pointed <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>. Not created; it is the CLI's.
    /// </summary>
    public static string UserClaudeHome =>
        Environment.GetEnvironmentVariable(ClaudeVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>The default Codex folder a terminal's codex uses: <c>CODEX_HOME</c> if set, else <c>~/.codex</c>. Not created.</summary>
    public static string UserCodexHome =>
        Environment.GetEnvironmentVariable(CodexVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>
    /// The Claude folder a background job runs under: Codale's own account once it is signed in,
    /// else the terminal's default one when that is, so a helper never prompts for a login the user already has.
    /// </summary>
    public static string HelperClaudeHome =>
        !File.Exists(Path.Combine(ClaudeHome, ".credentials.json")) &&
        File.Exists(Path.Combine(UserClaudeHome, ".credentials.json"))
            ? UserClaudeHome
            : ClaudeHome;

    /// <summary>The Codex counterpart of <see cref="HelperClaudeHome"/>.</summary>
    public static string HelperCodexHome =>
        !File.Exists(Path.Combine(CodexHome, "auth.json")) &&
        File.Exists(Path.Combine(UserCodexHome, "auth.json"))
            ? UserCodexHome
            : CodexHome;

    public const string ClaudeVariable = "CLAUDE_CONFIG_DIR";

    public const string CodexVariable = "CODEX_HOME";

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A physical folder every process sees at the same path. A packaged app's
    /// %LOCALAPPDATA% is virtualised: a folder it creates there lands in the package's
    /// local cache, so a login typed into a terminal would find nothing at that path.
    /// The local cache is where those writes already go, so earlier logins stay put.
    /// </summary>
    private static string ResolveRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = PackageFamilyName() is { } family
            ? Path.Combine(localAppData, "Packages", family, "LocalCache", "Local")
            : localAppData;

        return Path.Combine(root, "Codale", "accounts");
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