namespace Codale.Agents;

/// <summary>
/// Finds the Claude CLI the way the shell would: every PATH directory, every PATHEXT
/// extension (npm installs "claude.cmd", the native installer "claude.exe"), plus the
/// per-user install folders that a freshly installed CLI lands in before the app's
/// inherited PATH knows about them. Nothing is bundled into the app.
/// </summary>
/// <remarks>
/// A real executable always wins over a <c>.cmd</c>/<c>.bat</c> shim, and an npm shim is
/// resolved to the native binary it launches when the package ships one. A shim that
/// cannot be resolved is still returned (the CLI works), but it runs through cmd.exe,
/// which mangles quoted JSON and multi-line arguments - see <see cref="IsShim"/>.
/// </remarks>
public sealed record CliDescriptor(
    string Id,
    string DisplayName,
    string Executable,
    string? InstallCommand = null,
    string? Description = null)
{
    public string? ResolvedPath => CliLocator.Find(Executable);
    public bool IsInstalled => ResolvedPath is not null;
}

/// <summary>
/// Finds AI coding CLIs the way the shell would: every PATH directory, every PATHEXT
/// extension (npm shims, native binaries), plus per-user install folders.
/// </summary>
/// <remarks>
/// A real executable always wins over a <c>.cmd</c>/<c>.bat</c> shim, and an npm shim is
/// resolved to the native binary it launches when the package ships one. A shim that
/// cannot be resolved is still returned (the CLI works), but it runs through cmd.exe,
/// which mangles quoted JSON and multi-line arguments - see <see cref="IsShim"/>.
/// </remarks>
public static class CliLocator
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, (string? Path, DateTimeOffset Probed)> ProbeCache = new();

    /// <summary>The Claude CLI's command name.</summary>
    public const string Executable = "claude";

    /// <summary>The command that installs the Claude CLI, run in a terminal tab.</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>Known common agentic coding CLIs.</summary>
    public static readonly IReadOnlyList<CliDescriptor> KnownClis = new List<CliDescriptor>
    {
        new("claude", "Claude", "claude", "irm https://claude.ai/install.ps1 | iex", "Anthropic Claude Code CLI"),
        new("agy", "Antigravity", "agy", "npm i -g @google/antigravity", "Google Antigravity CLI"),
        new("gemini", "Gemini", "gemini", null, "Gemini Code Assist CLI"),
        new("copilot", "Copilot", "copilot", null, "GitHub Copilot CLI"),
        new("aider", "Aider", "aider", "pip install aider-chat", "Aider AI pair programmer CLI"),
        new("goose", "Goose", "goose", null, "Block Goose CLI"),
        new("codex", "Codex", "codex", null, "OpenAI Codex CLI"),
        new("cursor", "Cursor", "cursor", null, "Cursor CLI"),
    };

    /// <summary>Optional provider for the user's preferred CLI id (e.g. from AppSettings).</summary>
    public static Func<string?>? PreferredCliProvider { get; set; }

    /// <summary>Detects which known CLIs are installed on this machine.</summary>
    public static IReadOnlyList<CliDescriptor> DetectInstalledClis() =>
        KnownClis.Where(cli => cli.IsInstalled).ToList();

    /// <summary>
    /// The active CLI: checks user preference, else auto-detects installed CLIs
    /// (prioritizing Antigravity if found, then Claude, then first installed).
    /// </summary>
    public static CliDescriptor ActiveCli
    {
        get
        {
            var preferred = PreferredCliProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(preferred) && !string.Equals(preferred, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var match = KnownClis.FirstOrDefault(c => string.Equals(c.Id, preferred, StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(c.DisplayName, preferred, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            var installed = DetectInstalledClis();
            if (installed.Count > 0)
            {
                return installed.FirstOrDefault(c => c.Id == "agy")
                    ?? installed.FirstOrDefault(c => c.Id == "claude")
                    ?? installed[0];
            }

            return KnownClis[0];
        }
    }

    public static bool IsInstalled() => Find(Executable) is not null;

    /// <summary>
    /// Finds a CLI on PATH, cached for a few seconds. Each uncached probe reads the user
    /// and machine PATH out of the registry and stats every directory against every
    /// PATHEXT extension - hundreds of File.Exists calls - which workspace navigation
    /// was paying synchronously on the UI thread. A freshly installed CLI is still
    /// picked up within the TTL.
    /// </summary>
    public static string? Find(string name)
    {
        lock (ProbeCache)
        {
            if (ProbeCache.TryGetValue(name, out var hit) && DateTimeOffset.Now - hit.Probed < ProbeTtl)
            {
                return hit.Path;
            }
        }

        var found = FindUncached(name);

        lock (ProbeCache)
        {
            ProbeCache[name] = (found, DateTimeOffset.Now);
        }

        return found;
    }

    /// <summary>True for a batch-file shim: launched through cmd.exe, which re-parses the command line.</summary>
    public static bool IsShim(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string? FindUncached(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var directories = SearchDirectories().ToList();

        // Real executables across every directory first; a shim only if there is nothing else.
        string? shim = null;
        foreach (var wantShim in new[] { false, true })
        {
            foreach (var dir in directories)
            {
                foreach (var ext in extensions.Where(e => IsShim(e) == wantShim))
                {
                    try
                    {
                        var candidate = Path.Combine(dir, name + ext);
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        if (!wantShim)
                        {
                            return candidate;
                        }

                        shim ??= candidate;
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry; skip it.
                    }
                }
            }
        }

        return shim is null ? null : ResolveNpmShim(shim) ?? shim;
    }

    /// <summary>
    /// npm's <c>claude.cmd</c> sits beside <c>node_modules/@anthropic-ai/claude-code</c>; recent
    /// versions of that package ship the native binary under <c>bin</c>, which can be launched
    /// directly without cmd.exe in between.
    /// </summary>
    private static string? ResolveNpmShim(string shim)
    {
        try
        {
            if (Path.GetDirectoryName(shim) is not { } dir)
            {
                return null;
            }

            var native = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            return File.Exists(native) ? native : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        // Machine + user PATH from the registry as well as the process's own, so a CLI
        // installed while the app is running is found without a restart.
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return paths
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(appData, "npm"))
            .Append(Path.Combine(localAppData, "agy", "bin"))
            .Append(Path.Combine(home, ".gemini", "antigravity-cli", "bin"))
            .Select(d => Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')))
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
