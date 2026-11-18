using System.Globalization;
using System.Text.Json;

using Codale.Agents.Providers;



namespace Codale.Settings;

/// <summary>
/// The app-level preferences the Settings window edits: editor and terminal
/// typography, the default shell, chat defaults and the git refresh pace. Stored in
/// the hand-editable <see cref="FilePath"/> (settings.json); edits made to the file
/// while the app runs are picked up live. Values are cached after the first read, and
/// every write persists, refreshes the cache and raises <see cref="Changed"/> so open
/// windows can apply the new value live.
/// </summary>
/// <remarks>
/// All windows share one UI thread, and <see cref="Changed"/> is always raised on it
/// (see <see cref="AttachUiThread"/>), so subscribers need no marshalling. New objects
/// (an editor tab, a terminal, a git timer) read the properties at construction and
/// pick up whatever is current; the <see cref="Changed"/> event exists for the things
/// already alive when a setting turns. Write failures (read-only folder, file locked by
/// an editor) are logged and the value stays in effect in memory.
/// </remarks>
public sealed class AppSettingsStore(string folder) : IDisposable
{
    /// <summary>Editor font size bounds; matches the editor's own Ctrl+wheel zoom range.</summary>
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    /// <summary>Raised after any setting was written. Args are always <see cref="EventArgs.Empty"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Records the calling thread as the UI thread so <see cref="Changed"/> is posted back to it
    /// when a setting turns on another one (a file-watcher callback, a background job).
    /// </summary>
    public void AttachUiThread(Action<Action> dispatch, Func<bool> hasThreadAccess)
    {
        _dispatch = dispatch;
        _hasThreadAccess = hasThreadAccess;
    }

    /// <summary>Values are written with the invariant culture, so they parse back the same on any locale.</summary>
    private double ParseInvariant(string raw) => double.Parse(raw, CultureInfo.InvariantCulture);

    private int ParseInt(string raw) => int.Parse(raw, CultureInfo.InvariantCulture);

    private string FormatSize(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string FormatText(string value) => value;

    private string FormatFlag(bool value) => value ? "1" : "0";

    // Parsed values by settings key, guarded by FileLock (the file watcher clears them
    // from another thread). A hit is already validated and clamped.
    private readonly Dictionary<string, object> _cache = [with(StringComparer.Ordinal)];

    private T Get<T>(string key, Func<T> load) where T : notnull
    {
        lock (_fileLock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_cache.TryGetValue(key, out var hit))
            {
                return (T)hit;
            }

            var value = load();
            _cache[key] = value;
            return value;
        }
    }

    /// <summary>Persists <paramref name="value"/> (already clamped by the caller), refreshes the cache and tells listeners - unless it is already the current value.</summary>
    private void Set<T>(string key, T value, Func<T, string> format) where T : notnull
    {
        lock (_fileLock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_cache.TryGetValue(key, out var cached) && EqualityComparer<T>.Default.Equals((T)cached, value))
            {
                return;
            }

            Write(key, format(value));
            _cache[key] = value;
        }

        OnChanged();
    }

    private bool IsTheme(string raw) => raw is "System" or "Dark" or "Light";

    private bool IsShell(string raw) => raw is "auto" or "pwsh" or "powershell" or "cmd";

    /// <summary>
    /// The transcript's base text size; everything in the chat scales from it. 12.5 is
    /// the built-in look, so that value means "no change".
    /// </summary>
    public double ChatFontSize
    {
        get => Get("chat.fontSize", () => Read("chat.fontSize", 12.5d, ParseInvariant));
        set => Set("chat.fontSize", Math.Clamp(value, 9, 24), FormatSize);
    }

    /// <summary>The typeface the transcript reads in; code runs stay monospaced.</summary>
    public string ChatFontFamily
    {
        get => Get("chat.fontFamily", () => Read("chat.fontFamily", "Segoe UI"));
        set => Set("chat.fontFamily", string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim(), FormatText);
    }

    /// <summary>The app theme: "System" follows the Windows app mode, else "Dark" or "Light".</summary>
    public string Theme
    {
        get => Get("app.theme", () => Read("app.theme", "Dark") is var raw && IsTheme(raw) ? raw : "Dark");
        set => Set("app.theme", value is "System" or "Light" ? value : "Dark", FormatText);
    }

    /// <summary>The font size a new editor tab opens with.</summary>
    public double EditorFontSize
    {
        get => Get("editor.fontSize", () => Read("editor.fontSize", 13d, ParseInvariant));
        set => Set("editor.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>Spaces a Tab keypress inserts; existing text keeps its tabs.</summary>
    public int EditorTabWidth
    {
        get => Get("editor.tabWidth", () => Read("editor.tabWidth", 4, ParseInt));
        set => Set("editor.tabWidth", Math.Clamp(value, 1, 16), FormatInt);
    }

    /// <summary>Whether a shell command that trips none of the risk rules runs without asking (Plan mode aside).</summary>
    public bool AutoApproveSafeShell
    {
        get => Get("shell.autoApproveSafe", () => ReadFlag("shell.autoApproveSafe", true));
        set => Set("shell.autoApproveSafe", value, FormatFlag);
    }

    /// <summary>Whether agent sessions predict the next prompt and show it in the composer. Read when a session starts.</summary>
    public bool SuggestNextPrompt
    {
        get => Get("chat.suggestNextPrompt", () => ReadFlag("chat.suggestNextPrompt", true));
        set => Set("chat.suggestNextPrompt", value, FormatFlag);
    }

    /// <summary>
    /// Whether an idle chat compacts itself just before its prompt cache expires. Compacting
    /// while the cache is warm reads the history at the cache-hit price; once it expires the
    /// next prompt re-writes the whole history to the cache instead.
    /// </summary>
    public bool IdleCompact
    {
        get => Get("tokens.idleCompact", () => ReadFlag("tokens.idleCompact", true));
        set => Set("tokens.idleCompact", value, FormatFlag);
    }

    /// <summary>Idle seconds before that compaction: an hour, matching the 1-hour prompt cache lifetime.</summary>
    public int IdleCompactSeconds
    {
        get => Get("tokens.idleCompactSeconds", () => Read("tokens.idleCompactSeconds", 3600, ParseInt));
        set => Set("tokens.idleCompactSeconds", Math.Clamp(value, 60, 3600), FormatInt);
    }

    /// <summary>Context below this many tokens is not worth a summary: compacting it would save less than it costs.</summary>
    public int IdleCompactMinTokens
    {
        get => Get("tokens.idleCompactMinTokens", () => Read("tokens.idleCompactMinTokens", 50_000, ParseInt));
        set => Set("tokens.idleCompactMinTokens", Math.Max(0, value), FormatInt);
    }

    private bool IsSmartProvider(string provider) => SmartFeatureSettings.IsProvider(provider);

    /// <summary>The provider a Smart model role runs on: "claude", "codex", "openai", "anthropic" or "off".</summary>
    public string SmartProviderFor(string role) =>
        Get($"ai.smart.groups.{role}.provider", () => Read($"ai.smart.groups.{role}.provider", "off")) is var provider
            && IsSmartProvider(provider) ? provider : "off";

    public void SetSmartProvider(string role, string provider) =>
        Set($"ai.smart.groups.{role}.provider", IsSmartProvider(provider) ? provider : "off", FormatText);

    public bool SmartExperience => SmartFeatureSettings.Roles.Any(r => SmartRoleEnabled(r.Id));

    /// <summary>The model chosen for a role on its current provider; empty means the profile's Flash model.</summary>
    public string SmartRoleModel(string role) =>
        Get($"ai.smart.{SmartProviderFor(role)}.groups.{role}.model",
            () => Read($"ai.smart.{SmartProviderFor(role)}.groups.{role}.model", "default")) is var model
            && model.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : model;

    public void SetSmartRoleModel(string role, string model) =>
        Set($"ai.smart.{SmartProviderFor(role)}.groups.{role}.model",
            string.IsNullOrWhiteSpace(model) || model.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : model.Trim(), FormatText);

    public string SmartApiProfileFor(string role) =>
        Get($"ai.smart.groups.{role}.{SmartProviderFor(role)}.profile",
            () => Read($"ai.smart.groups.{role}.{SmartProviderFor(role)}.profile", ""));

    public void SetSmartApiProfile(string role, string profile) =>
        Set($"ai.smart.groups.{role}.{SmartProviderFor(role)}.profile", profile, FormatText);

    /// <summary>Whether a role is configured; dedicated roles (Auto Complete, Embedding) also need an endpoint and an explicit model.</summary>
    public bool SmartRoleEnabled(string role)
    {
        var provider = SmartProviderFor(role);
        var model = SmartRoleModel(role);
        return provider != "off" && !model.Equals("off", StringComparison.OrdinalIgnoreCase)
            && SmartFeatureSettings.SupportsProvider(role, provider)
            && (!SmartFeatureSettings.RequiresDedicatedModel(role) || model.Length > 0);
    }

    /// <summary>Whether any role the task can run on is configured.</summary>
    public bool SmartFeatureEnabled(string task) => SmartFeatureSettings.RolesFor(task).Any(SmartRoleEnabled)
        || SmartFeatureSettings.IsDecisionTask(task) && SmartRoleEnabled(SmartFeatureSettings.Decision);
    public bool SmartThinking => false;
    public bool SmartCompletions => SmartFeatureEnabled("completion");
    /// <summary>Whether the editor draws the gutter with line numbers.</summary>
    public bool EditorLineNumbers
    {
        get => Get("editor.lineNumbers", () => ReadFlag("editor.lineNumbers", true));
        set => Set("editor.lineNumbers", value, FormatFlag);
    }

    /// <summary>Whether tokens colour the editor text, or everything draws plain.</summary>
    public bool EditorHighlighting
    {
        get => Get("editor.highlighting", () => ReadFlag("editor.highlighting", true));
        set => Set("editor.highlighting", value, FormatFlag);
    }

    /// <summary>Whether long editor lines fold at the viewport edge instead of scrolling sideways.</summary>
    public bool EditorWordWrap
    {
        get => Get("editor.wordWrap", () => ReadFlag("editor.wordWrap", false));
        set => Set("editor.wordWrap", value, FormatFlag);
    }

    private const string SyntaxOverridesKey = "editor.syntaxOverrides";

    /// <summary>
    /// Files whose language the user picked by hand, by full path to language id ("plaintext" turns
    /// colouring off). Stored as a JSON object inside the one string value so the file stays flat.
    /// The returned dictionary is a copy.
    /// </summary>
    public Dictionary<string, string> SyntaxOverrides
    {
        get
        {
            var raw = Get(SyntaxOverridesKey, () => Read(SyntaxOverridesKey, "{}"));
            try
            {
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [],
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return [with(StringComparer.OrdinalIgnoreCase)];
            }
        }
        set => Set(SyntaxOverridesKey, JsonSerializer.Serialize(value), FormatText);
    }

    /// <summary>
    /// The shell new terminal tabs run: "auto" (pwsh, then Windows PowerShell, then
    /// cmd), or a named choice that falls back to the automatic probe when missing.
    /// </summary>
    public string TerminalShell
    {
        get => Get("terminal.shell", () => Read("terminal.shell", "auto") is var raw && IsShell(raw) ? raw : "auto");
        set => Set("terminal.shell", IsShell(value) ? value : "auto", FormatText);
    }

    /// <summary>
    /// The terminal's monospace family. Readers validate it against the installed
    /// fonts and fall back to Cascadia Mono, so a missing family cannot silently
    /// degrade to a proportional one and break the cell grid.
    /// </summary>
    public string TerminalFontFamily
    {
        get => Get("terminal.fontFamily", () => Read("terminal.fontFamily", "Cascadia Mono"));
        set => Set("terminal.fontFamily", string.IsNullOrWhiteSpace(value) ? "Cascadia Mono" : value.Trim(), FormatText);
    }

    /// <summary>The font size a new terminal tab opens with (live change refits open ones).</summary>
    public double TerminalFontSize
    {
        get => Get("terminal.fontSize", () => Read("terminal.fontSize", 13d, ParseInvariant));
        set => Set("terminal.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>How many scrolled-off lines a terminal keeps, applied at session start.</summary>
    public int TerminalScrollback
    {
        get => Get("terminal.scrollback", () => Read("terminal.scrollback", 10_000, ParseInt));
        set => Set("terminal.scrollback", Math.Clamp(value, 100, 1_000_000), FormatInt);
    }

    /// <summary>Lines a command block shows before it scrolls inside; the default matches a 120 × 30 terminal.</summary>
    public int TerminalBlockLines
    {
        get => Get("terminal.blockLines", () => Math.Clamp(Read("terminal.blockLines", 30, ParseInt), 3, 200));
        set => Set("terminal.blockLines", Math.Clamp(value, 3, 200), FormatInt);
    }

    /// <summary>Command blocks kept in a terminal's history; the oldest finished ones beyond this are dropped.</summary>
    public int TerminalBlockHistory
    {
        get => Get("terminal.blockHistory", () => Math.Clamp(Read("terminal.blockHistory", 200, ParseInt), 10, 5_000));
        set => Set("terminal.blockHistory", Math.Clamp(value, 10, 5_000), FormatInt);
    }

    /// <summary>Whether each PowerShell command block loads the user's profile first (slower: it runs every time).</summary>
    public bool TerminalLoadProfile
    {
        get => Get("terminal.loadProfile", () => ReadFlag("terminal.loadProfile", false));
        set => Set("terminal.loadProfile", value, FormatFlag);
    }

    private bool ReadFlag(string name, bool fallback) => Read(name, fallback ? "1" : "0") == "1";

    private bool IsChatMode(string? mode) =>
        mode is "automatic" or "ask" or "plan" or "manual" or "acceptEdits" or "auto";

    /// <summary>The permission mode a fresh chat starts in: "automatic" (Auto), "ask", "plan", "manual", "acceptEdits" or "auto" (Full). Unset is Auto: a CLI left in its own default asks about every edit.</summary>
    public string DefaultChatMode
    {
        get
        {
            EnsureFullModeMigration();
            return Get("chat.defaultMode", () => Read("chat.defaultMode", "") is var raw && IsChatMode(raw) ? raw : "auto");
        }
        set => Set("chat.defaultMode", IsChatMode(value) ? value : "auto", FormatText);
    }

    /// <summary>One-time reset of saved default modes so every CLI starts in Full (YOLO).</summary>
    private void EnsureFullModeMigration()
    {
        lock (_fileLock)
        {
            if (Read("chat.fullModeMigrated", "") == "1")
            {
                return;
            }

            foreach (var key in new[] { "chat.defaultMode", DefaultsKey("claude", "mode"), DefaultsKey("codex", "mode") })
            {
                Write(key, "");
                _cache.Remove(key);
            }

            Write("chat.fullModeMigrated", "1");
        }
    }

    /// <summary>The last model/effort/mode the user picked for a CLI ("claude" or "codex"), saved as
    /// the default its new chats start with. An empty stored value (or null) means unset: the CLI's
    /// own default. Model and effort ids are CLI-specific, so the defaults are kept per CLI.</summary>
    private string DefaultsKey(string cli, string leaf) => $"chat.defaults.{cli}.{leaf}";

    private string? ReadDefault(string cli, string leaf) =>
        Get(DefaultsKey(cli, leaf), () => Read(DefaultsKey(cli, leaf), "")) is { Length: > 0 } stored ? stored : null;

    private void WriteDefault(string cli, string leaf, string? value)
    {
        var key = DefaultsKey(cli, leaf);
        var raw = value is { Length: > 0 } ? value : "";
        lock (_fileLock)
        {
            if (Read(key, "") == raw)
            {
                return;
            }

            Write(key, raw);
            // ReadDefault answers from the cache, so it must see the new value too.
            _cache[key] = raw;
        }

        OnChanged();
    }

    /// <summary>The model tier a fresh chat starts with: "cli", "lite", "flash", "pro" or "plus". Null means "cli".</summary>
    private bool IsTier(string? tier) => tier is "cli" or "lite" or "flash" or "pro" or "plus";

    public string DefaultTierFor(string cli) =>
        ReadDefault(cli, "tier") is { } saved && IsTier(saved) ? saved : "cli";

    public void SetDefaultTier(string cli, string? tier) => WriteDefault(cli, "tier", IsTier(tier) ? tier : "");

    public (bool HasValue, string? Model, string? Effort) ModelPreferenceFor(string cli) =>
        (ReadDefault(cli, "modelChosen") == "true", ReadDefault(cli, "model"), ReadDefault(cli, "effort"));

    public void SetModelPreference(string cli, string? model, string? effort)
    {
        WriteDefault(cli, "model", model);
        WriteDefault(cli, "effort", effort);
        WriteDefault(cli, "modelChosen", "true");
    }

    /// <summary>The provider new chats of a CLI connect through: a name from <see cref="ByokProviders"/>, or "" for the CLI's own login.</summary>
    public string CliProviderFor(string cli) =>
        ReadDefault(cli, "provider") is { } name && FindByok(name)?.Supports(cli) == true ? name : "";

    public string DefaultChatCli => Read("chat.lastCli", "") is { } saved && saved is ("claude" or "codex")
        ? saved
        : ByokProviders.FirstOrDefault() is { } profile
            ? profile.Supports("claude") ? "claude" : "codex"
            : Codale.Agents.CliLocator.ActiveCli.Id;

    public void RememberProfile(string cli, string name)
    {
        SetCliProvider(cli, name);
        Set("chat.lastCli", cli, FormatText);
    }

    public async Task<(string Cli, string Provider)> ResolveChatProfileAsync()
    {
        var cli = DefaultChatCli;
        var provider = CliProviderFor(cli);
        if (Read("chat.lastCli", "") is "claude" or "codex") return (cli, provider);
        if (FindByok(provider) is not null) return (cli, provider);
        if (ByokProviders.FirstOrDefault(p => p.Supports(cli)) is { } configured) return (cli, configured.Key);
        foreach (var candidate in new[] { cli, cli == "claude" ? "codex" : "claude" })
        {
            var accounts = await Codale.Agents.CodaleAccountCli.DiscoverAccountsAsync(candidate);
            if (accounts.FirstOrDefault() is not { } account) continue;
            if (account.AccountId.Length == 0) return (candidate, "");
            var profiles = ByokProviders;
            var detected = new ByokProvider
            {
                Id = Guid.NewGuid().ToString("N"),
                CliId = candidate,
                IsAccount = true,
                AccountId = account.AccountId,
                Email = account.Email,
                Name = account.Email ?? "Detected account"
            };
            profiles.Add(detected);
            ByokProviders = profiles;
            return (candidate, detected.Key);
        }
        return (cli, provider);
    }
    public string AccountLabel(string cli) => Read("account." + cli + ".email", "Sign-in account");
    public void SetAccountLabel(string cli, string? email) => Set("account." + cli + ".email", email ?? "Sign-in account", FormatText);

    public void SetCliProvider(string cli, string? name) => WriteDefault(cli, "provider", name?.Trim());

    /// <summary>The saved mode for a CLI, falling back to the global <see cref="DefaultChatMode"/> when unset.</summary>
    public string DefaultModeFor(string cli)
    {
        EnsureFullModeMigration();
        return ReadDefault(cli, "mode") is { } saved && IsChatMode(saved) ? saved : DefaultChatMode;
    }

    public void SetDefaultMode(string cli, string? value) => WriteDefault(cli, "mode", IsChatMode(value) ? value : "");

    /// <summary>Seconds between the git panel's working-tree checks.</summary>
    public int GitPollSeconds
    {
        get => Get("git.pollSeconds", () => Read("git.pollSeconds", 5, ParseInt));
        set => Set("git.pollSeconds", Math.Clamp(value, 1, 600), FormatInt);
    }

    private const string ProvidersKey = "byok.providers";

    private readonly JsonSerializerOptions _providerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private ByokProvider Clone(ByokProvider p, string? apiKey = null) => new()
    {
        Id = p.Id,
        Name = p.Name,
        CliId = p.CliId,
        IsAccount = p.IsAccount,
        AccountId = p.AccountId,
        Email = p.Email,
        BaseUrl = p.BaseUrl,
        AnthropicBaseUrl = p.AnthropicBaseUrl,
        ApiKey = apiKey ?? p.ApiKey,
        FlashModel = p.FlashModel,
        ProModel = p.ProModel,
        ModelContextWindows = p.ModelContextWindows is not null
            ? new(p.ModelContextWindows, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Serialises providers for the file (<paramref name="protect"/>: keys DPAPI-wrapped) or, unprotected, for change comparison.</summary>
    private string SerializeProviders(IEnumerable<ByokProvider> providers, bool protect) =>
        JsonSerializer.Serialize(
            providers.Select(p => Clone(p, protect ? SecretProtector.Protect(p.ApiKey) : p.ApiKey)).ToList(),
            _providerJson);

    /// <summary>Drops nameless and duplicate (by trimmed name) entries.</summary>
    private List<ByokProvider> Normalize(IEnumerable<ByokProvider> providers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. providers.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Key))];
    }

    /// <summary>The cached providers (decrypted); callers copy before handing them out. Call under <see cref="_fileLock"/>.</summary>
    private List<ByokProvider> Providers() => Get(ProvidersKey, LoadProviders);

    private List<ByokProvider> LoadProviders() => ParseProviders(Read(ProvidersKey, "[]"));

    /// <summary>
    /// The BYOK providers listed in settings.json. Nameless or duplicate entries are
    /// dropped. The returned list is a copy: assign it back to persist changes.
    /// </summary>
    public List<ByokProvider> ByokProviders
    {
        get
        {
            lock (_fileLock)
            {
                return [.. Providers().Select(p => Clone(p))];
            }
        }
        set
        {
            var clean = Normalize(value.Select(p => Clone(p)));
            lock (_fileLock)
            {
                if (SerializeProviders(clean, protect: false) == SerializeProviders(Providers(), protect: false))
                {
                    return;
                }

                Write(ProvidersKey, SerializeProviders(clean, protect: true));
                _cache[ProvidersKey] = clean;
            }

            OnChanged();
        }
    }

    private List<ByokProvider> ParseProviders(string json)
    {
        try
        {
            var parsed = Normalize(JsonSerializer.Deserialize<List<ByokProvider>>(json, _providerJson) ?? []);
            foreach (var provider in parsed)
            {
                var stored = provider.ApiKey ?? "";
                provider.ApiKey = SecretProtector.Unprotect(stored);
            }

            return parsed;
        }
        catch (Exception)
        {
            // Hand-edited into something unparseable: no providers rather than a crash.
            return [];
        }
    }

    /// <summary>A provider by name (a copy), or null for Default ("") or a name that no longer exists.</summary>
    public ByokProvider? FindByok(string? name)
    {
        var wanted = name?.Trim() ?? "";
        if (wanted.Length == 0)
        {
            return null;
        }

        lock (_fileLock)
        {
            var found = Providers().FirstOrDefault(p => string.Equals(p.Key, wanted, StringComparison.OrdinalIgnoreCase));
            return found is null ? null : Clone(found);
        }
    }

    /// <summary>The chord stored for a shortcut action ("shortcut.&lt;id&gt;"), or null while it keeps its default.</summary>
    public string? GetShortcut(string id) =>
        Read("shortcut." + id, "") is { Length: > 0 } stored ? stored : null;

    /// <summary>Stores a shortcut override; null drops it so the action follows its default again.</summary>
    public void SetShortcut(string id, string? chord)
    {
        var key = "shortcut." + id;
        lock (_fileLock)
        {
            var values = Values;
            if (chord is null ? !values.Remove(key) : values.TryGetValue(key, out var current) && current == chord)
            {
                return;
            }

            if (chord is not null)
            {
                values[key] = chord;
            }

            Persist(values);
        }

        OnChanged();
    }

    /// <summary>Raises <see cref="Changed"/> on the UI thread, posting when called from another one.</summary>
    private void OnChanged()
    {
        if (_stopped) return;
        if (_dispatch is { } dispatch && _hasThreadAccess?.Invoke() == false)
        {
            dispatch(() => { if (!_stopped) Changed?.Invoke(this, EventArgs.Empty); });
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The hand-editable settings file, next to the database:
    /// <c>%LOCALAPPDATA%\Codale\settings.json</c> (redirected into the package folder under MSIX).
    /// </summary>
    public string DataFolder => Path.GetDirectoryName(FilePath)!;
    private string FilePath { get; } = Path.Combine(Path.GetFullPath(folder), "settings.json");

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>Keys whose JSON value is a boolean; everything else numeric is written as a number, the rest as strings.</summary>
    private readonly HashSet<string> _boolKeys =
    [
        "editor.lineNumbers", "editor.highlighting", "editor.wordWrap", "chat.suggestNextPrompt",
        "tokens.compressShell", "tokens.digest", "tokens.explore", "tokens.readGuard", "tokens.handoffNudge", "tokens.terse",
        "tokens.idleCompact",
    ];

    private readonly HashSet<string> _numberKeys =
    [
        "editor.fontSize", "editor.tabWidth", "terminal.fontSize", "terminal.scrollback", "chat.fontSize", "git.pollSeconds",
        "tokens.autoCompactPercent", "tokens.idleCompactSeconds", "tokens.idleCompactMinTokens",
    ];

    private readonly Lock _fileLock = new();
    private SortedDictionary<string, string>? _values;
    private string _lastWritten = "";
    private Action<Action>? _dispatch;
    private Func<bool>? _hasThreadAccess;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private volatile bool _stopped;

    public void Dispose()
    {
        lock (_fileLock)
        {
            _stopped = true;
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
            Changed = null;
            _dispatch = null;
            _hasThreadAccess = null;
            _values = null;
            _cache.Clear();
        }
    }

    private void ScheduleReload()
    {
        lock (_fileLock)
        {
            if (!_stopped) _debounce?.Change(150, Timeout.Infinite);
        }
    }

    private SortedDictionary<string, string> Values
    {
        get
        {
            lock (_fileLock)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (_values is null)
                {
                    _values = LoadFile() ?? SeedDefaults();
                    if (MigrateSmartRoles(_values)) Persist(_values);
                    if (!_stopped) StartWatching();
                }

                return _values;
            }
        }
    }

    /// <summary>Parses the file; null when it is missing or unreadable (defaults stand for a broken file).</summary>
    private SortedDictionary<string, string>? LoadFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var text = File.ReadAllText(FilePath);
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in doc.RootElement.EnumerateObject())
            {

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.True:
                        result[property.Name] = "1";
                        break;
                    case JsonValueKind.False:
                        result[property.Name] = "0";
                        break;
                    case JsonValueKind.Number:
                        result[property.Name] = property.Value.GetRawText();
                        break;
                    case JsonValueKind.String:
                        result[property.Name] = property.Value.GetString() ?? "";
                        break;
                    case JsonValueKind.Array when property.Name == ProvidersKey:
                        result[property.Name] = property.Value.GetRawText();
                        break;
                }
            }

            _lastWritten = text;
            return result;
        }
        catch (Exception)
        {
            // Half-typed JSON mid-edit: keep the current values (or defaults on first load).
            return _values ?? [with(StringComparer.Ordinal)];
        }
    }

    /// <summary>One-time move from the old Smart groups to model roles (see <see cref="SmartFeatureSettings.MigrateLegacyGroups"/>).</summary>
    private bool MigrateSmartRoles(IDictionary<string, string> values) => SmartFeatureSettings.MigrateLegacyGroups(values);

    /// <summary>First run: a settings file with every key spelled out, so it shows what can be configured.</summary>
    private SortedDictionary<string, string> SeedDefaults()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // Spell out every key so the freshly created file shows what can be configured.
        foreach (var (key, value) in new[]
        {
            ("app.theme", "Dark"), ("editor.fontSize", "13"), ("editor.tabWidth", "4"), ("editor.lineNumbers", "1"),
            ("editor.highlighting", "1"), ("editor.wordWrap", "0"), ("terminal.shell", "auto"),
            ("terminal.fontFamily", "Cascadia Mono"), ("terminal.fontSize", "13"), ("terminal.scrollback", "10000"),
            ("chat.fontSize", "12.5"), ("chat.fontFamily", "Segoe UI"),
            ("chat.defaultMode", " "), ("chat.defaultModel", " "), ("chat.defaultEffort", " "),
            ("git.pollSeconds", "5"), ("ai.smart.groups.general.provider", "off"),
            ("ai.smart.groups.completion.provider", "off"), ("ai.smart.groups.embedding.provider", "off"),
            ("ai.smart.groups.decision.provider", "off"), (SmartFeatureSettings.MigratedKey, "1"),
        })
        {
            result.TryAdd(key, value);
        }

        result["chat.defaultMode"] = result["chat.defaultMode"].Trim();
        result["chat.defaultModel"] = result["chat.defaultModel"].Trim();
        result["chat.defaultEffort"] = result["chat.defaultEffort"].Trim();

        result[ProvidersKey] = "[]";
        Persist(result);
        return result;
    }

    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Editors write in bursts; each event pushes the single reload back by 150 ms.
            _debounce = new System.Threading.Timer(_ => OnFileEdited(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
        }
        catch (Exception)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
            // No live reload; edits apply on next start.
        }
    }

    /// <summary>A hand edit landed: reload, drop the caches and tell open windows.</summary>
    private void OnFileEdited()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception)
        {
            return;
        }

        lock (_fileLock)
        {
            if (_stopped || text == _lastWritten)
            {
                return; // our own write
            }

            var fresh = LoadFile();
            if (fresh is null)
            {
                return;
            }

            _values = fresh;
            _cache.Clear();
        }

        OnChanged();
    }

    private string Read(string name, string fallback)
    {
        // The file watcher swaps and Write mutates the dictionary from other threads.
        lock (_fileLock)
        {
            return Values.TryGetValue(name, out var raw) && raw.Length > 0 ? raw : fallback;
        }
    }

    private T Read<T>(string name, T fallback, Func<string, T> parse)
    {
        var raw = Read(name, "");
        if (raw.Length == 0)
        {
            return fallback;
        }

        try
        {
            return parse(raw);
        }
        catch (Exception)
        {
            // A corrupt or out-of-range stored value falls back to the default.
            return fallback;
        }
    }

    private void Write(string name, string value)
    {
        lock (_fileLock)
        {
            var values = Values;
            values[name] = value;
            Persist(values);
        }
    }

    /// <summary>Writes the file through a temp file and a replace, so a crash mid-write never leaves half a settings.json. Failures are logged; the in-memory values stand.</summary>
    private void Persist(SortedDictionary<string, string> values)
    {
        var text = Serialize(values);
        _lastWritten = text;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(FilePath))
            {
                File.Replace(temp, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Renders the values as typed JSON: booleans and numbers as such, the rest as strings.</summary>
    private string Serialize(SortedDictionary<string, string> values)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            if (key == ProvidersKey)
            {
                using var providers = JsonDocument.Parse(raw);
                root[key] = providers.RootElement.Clone();
                continue;
            }

            root[key] = _boolKeys.Contains(key) ? raw == "1"
                : _numberKeys.Contains(key) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
                : raw;
        }

        return JsonSerializer.Serialize(root, _jsonOptions);
    }
}
