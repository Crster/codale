using System.Globalization;
using System.Text.Json;

using Codale.Agents.Providers;
using Codale.Db;

namespace Codale.Settings;

/// <summary>
/// The app-level preferences the Settings window edits: editor and terminal
/// typography, the default shell, chat defaults and the git refresh pace. Preferences
/// are typed database records. Committed changes are reflected in open windows.
/// </summary>
/// <remarks>
/// All windows share one UI thread, and <see cref="Changed"/> is always raised on it
/// (see <see cref="AttachUiThread"/>), so subscribers need no marshalling. New objects
/// (an editor tab, a terminal, a git timer) read the properties at construction and
/// pick up whatever is current; the <see cref="Changed"/> event exists for the things
/// already alive when a setting turns.
/// </remarks>
public sealed class AppSettingsStore : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ApplicationDatabase _database;
    private readonly ApplicationDatabase? _ownedDatabase;
    private readonly IDisposable _watch;
    private readonly Dictionary<string, string?> _announced = new(StringComparer.Ordinal);
    private Action<Action>? _dispatch;
    private Func<bool>? _hasThreadAccess;
    private volatile bool _stopped;

    public AppSettingsStore(string folder) : this(new ApplicationDatabase(folder), ownsDatabase: true) { }
    public AppSettingsStore(ApplicationDatabase database) : this(database, ownsDatabase: false) { }

    private AppSettingsStore(ApplicationDatabase database, bool ownsDatabase)
    {
        _database = database;
        _ownedDatabase = ownsDatabase ? database : null;
        DataFolder = database.Folder;
        _watch = database.WatchSettings(OnRecordChanged);
    }

    public string DataFolder { get; }
    /// <summary>Editor font size bounds; matches the editor's own Ctrl+wheel zoom range.</summary>
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    /// <summary>Raised after any setting was written. Args are always <see cref="EventArgs.Empty"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Records the calling thread as the UI thread so <see cref="Changed"/> is posted back to it
    /// when a setting changes on another thread.
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

    private void Set<T>(string key, T value, Func<T, string> format) => Save(key, format(value));

    private bool IsTheme(string raw) => raw is "System" or "Dark" or "Light";

    private bool IsShell(string raw) => raw is "auto" or "pwsh" or "powershell" or "cmd";

    /// <summary>
    /// The transcript's base text size; everything in the chat scales from it. 12.5 is
    /// the built-in look, so that value means "no change".
    /// </summary>
    public double ChatFontSize
    {
        get => Read("chat.fontSize", 12.5d, ParseInvariant);
        set => Set("chat.fontSize", Math.Clamp(value, 9, 24), FormatSize);
    }

    /// <summary>The typeface the transcript reads in; code runs stay monospaced.</summary>
    public string ChatFontFamily
    {
        get => Read("chat.fontFamily", "Segoe UI");
        set => Set("chat.fontFamily", string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim(), FormatText);
    }

    /// <summary>The app theme: "System" follows the Windows app mode, else "Dark" or "Light".</summary>
    public string Theme
    {
        get => Read("app.theme", "Dark") is var raw && IsTheme(raw) ? raw : "Dark";
        set => Set("app.theme", value is "System" or "Light" ? value : "Dark", FormatText);
    }

    /// <summary>The font size a new editor tab opens with.</summary>
    public double EditorFontSize
    {
        get => Read("editor.fontSize", 13d, ParseInvariant);
        set => Set("editor.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>Spaces a Tab keypress inserts; existing text keeps its tabs.</summary>
    public int EditorTabWidth
    {
        get => Read("editor.tabWidth", 4, ParseInt);
        set => Set("editor.tabWidth", Math.Clamp(value, 1, 16), FormatInt);
    }

    /// <summary>Whether a shell command that trips none of the risk rules runs without asking (Plan mode aside).</summary>
    public bool AutoApproveSafeShell
    {
        get => ReadFlag("shell.autoApproveSafe", true);
        set => Set("shell.autoApproveSafe", value, FormatFlag);
    }

    /// <summary>Whether agent sessions predict the next prompt and show it in the composer. Read when a session starts.</summary>
    public bool SuggestNextPrompt
    {
        get => ReadFlag("chat.suggestNextPrompt", true);
        set => Set("chat.suggestNextPrompt", value, FormatFlag);
    }

    /// <summary>
    /// Whether an idle chat compacts itself just before its prompt cache expires. Compacting
    /// while the cache is warm reads the history at the cache-hit price; once it expires the
    /// next prompt re-writes the whole history to the cache instead.
    /// </summary>
    public bool IdleCompact
    {
        get => ReadFlag("tokens.idleCompact", true);
        set => Set("tokens.idleCompact", value, FormatFlag);
    }

    /// <summary>Idle seconds before that compaction: an hour, matching the 1-hour prompt cache lifetime.</summary>
    public int IdleCompactSeconds
    {
        get => Read("tokens.idleCompactSeconds", 3600, ParseInt);
        set => Set("tokens.idleCompactSeconds", Math.Clamp(value, 60, 3600), FormatInt);
    }

    /// <summary>Context below this many tokens is not worth a summary: compacting it would save less than it costs.</summary>
    public int IdleCompactMinTokens
    {
        get => Read("tokens.idleCompactMinTokens", 50_000, ParseInt);
        set => Set("tokens.idleCompactMinTokens", Math.Max(0, value), FormatInt);
    }

    private bool IsSmartProvider(string provider) => SmartFeatureSettings.IsProvider(provider);

    /// <summary>The provider a Smart model role runs on: "claude", "codex", "openai", "anthropic" or "off".</summary>
    public string SmartProviderFor(string role) =>
        Read($"ai.smart.groups.{role}.provider", "off") is var provider
            && IsSmartProvider(provider) ? provider : "off";

    public void SetSmartProvider(string role, string provider) =>
        Set($"ai.smart.groups.{role}.provider", IsSmartProvider(provider) ? provider : "off", FormatText);

    public bool SmartExperience => SmartFeatureSettings.Roles.Any(r => SmartRoleEnabled(r.Id));

    /// <summary>The model chosen for a role on its current provider; empty means the profile's Flash model.</summary>
    public string SmartRoleModel(string role) =>
        Read($"ai.smart.{SmartProviderFor(role)}.groups.{role}.model", "default") is var model
            && model.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : model;

    public void SetSmartRoleModel(string role, string model) =>
        Set($"ai.smart.{SmartProviderFor(role)}.groups.{role}.model",
            string.IsNullOrWhiteSpace(model) || model.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : model.Trim(), FormatText);

    public string SmartApiProfileFor(string role) =>
        Read($"ai.smart.groups.{role}.{SmartProviderFor(role)}.profile", "");

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
        get => ReadFlag("editor.lineNumbers", true);
        set => Set("editor.lineNumbers", value, FormatFlag);
    }

    /// <summary>Whether tokens colour the editor text, or everything draws plain.</summary>
    public bool EditorHighlighting
    {
        get => ReadFlag("editor.highlighting", true);
        set => Set("editor.highlighting", value, FormatFlag);
    }

    public string EditorDarkSyntaxTheme
    {
        get => Read("editor.darkSyntaxTheme", "");
        set => Set("editor.darkSyntaxTheme", value, FormatText);
    }

    public string EditorLightSyntaxTheme
    {
        get => Read("editor.lightSyntaxTheme", "");
        set => Set("editor.lightSyntaxTheme", value, FormatText);
    }

    /// <summary>Whether long editor lines fold at the viewport edge instead of scrolling sideways.</summary>
    public bool EditorWordWrap
    {
        get => ReadFlag("editor.wordWrap", false);
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
            var raw = Read(SyntaxOverridesKey, "{}");
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
        get => Read("terminal.shell", "auto") is var raw && IsShell(raw) ? raw : "auto";
        set => Set("terminal.shell", IsShell(value) ? value : "auto", FormatText);
    }

    /// <summary>
    /// The terminal's monospace family. Readers validate it against the installed
    /// fonts and fall back to Cascadia Mono, so a missing family cannot silently
    /// degrade to a proportional one and break the cell grid.
    /// </summary>
    public string TerminalFontFamily
    {
        get => Read("terminal.fontFamily", "Cascadia Mono");
        set => Set("terminal.fontFamily", string.IsNullOrWhiteSpace(value) ? "Cascadia Mono" : value.Trim(), FormatText);
    }

    /// <summary>The font size a new terminal tab opens with (live change refits open ones).</summary>
    public double TerminalFontSize
    {
        get => Read("terminal.fontSize", 13d, ParseInvariant);
        set => Set("terminal.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>How many scrolled-off lines a terminal keeps, applied at session start.</summary>
    public int TerminalScrollback
    {
        get => Read("terminal.scrollback", 10_000, ParseInt);
        set => Set("terminal.scrollback", Math.Clamp(value, 100, 1_000_000), FormatInt);
    }

    /// <summary>Lines a command block shows before it scrolls inside; the default matches a 120 × 30 terminal.</summary>
    public int TerminalBlockLines
    {
        get => Math.Clamp(Read("terminal.blockLines", 30, ParseInt), 3, 200);
        set => Set("terminal.blockLines", Math.Clamp(value, 3, 200), FormatInt);
    }

    /// <summary>Command blocks kept in a terminal's history; the oldest finished ones beyond this are dropped.</summary>
    public int TerminalBlockHistory
    {
        get => Math.Clamp(Read("terminal.blockHistory", 200, ParseInt), 10, 5_000);
        set => Set("terminal.blockHistory", Math.Clamp(value, 10, 5_000), FormatInt);
    }

    /// <summary>Whether each PowerShell command block loads the user's profile first (slower: it runs every time).</summary>
    public bool TerminalLoadProfile
    {
        get => ReadFlag("terminal.loadProfile", false);
        set => Set("terminal.loadProfile", value, FormatFlag);
    }

    private bool ReadFlag(string name, bool fallback) => Read(name, fallback ? "1" : "0") == "1";

    private bool IsChatMode(string? mode) =>
        mode is "automatic" or "ask" or "plan" or "manual" or "auto";

    /// <summary>The permission mode for new chats. Unset follows the CLI default.</summary>
    public string DefaultChatMode
    {
        get => Read("chat.defaultMode", "") is var mode && IsChatMode(mode) ? mode : "auto";
        set => Set("chat.defaultMode", IsChatMode(value) ? value : "auto", FormatText);
    }

    /// <summary>The last model/effort/mode the user picked for a CLI ("claude" or "codex"), saved as
    /// the default its new chats start with. An empty stored value (or null) means unset: the CLI's
    /// own default. Model and effort ids are CLI-specific, so the defaults are kept per CLI.</summary>
    private string DefaultsKey(string cli, string leaf) => $"chat.defaults.{cli}.{leaf}";

    private string? ReadDefault(string cli, string leaf) =>
        Read(DefaultsKey(cli, leaf), "") is { Length: > 0 } stored ? stored : null;

    private void WriteDefault(string cli, string leaf, string? value) => Save(DefaultsKey(cli, leaf), value);

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
    public string DefaultModeFor(string cli) => ReadDefault(cli, "mode") is { } saved && IsChatMode(saved) ? saved : DefaultChatMode;

    public void SetDefaultMode(string cli, string? value) => WriteDefault(cli, "mode", IsChatMode(value) ? value : "");

    /// <summary>Seconds between the git panel's working-tree checks.</summary>
    public int GitPollSeconds
    {
        get => Read("git.pollSeconds", 5, ParseInt);
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

    /// <summary>Serializes providers with protected keys for storage or with unprotected keys for change comparison.</summary>
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

    private string? _providerSource;
    private List<ByokProvider> _providers = [];

    private List<ByokProvider> Providers()
    {
        var source = Read(ProvidersKey, "[]");
        if (_providerSource != source)
        {
            _providers = ParseProviders(source);
            _providerSource = source;
        }
        return _providers;
    }

    /// <summary>
    /// The saved BYOK providers. Nameless or duplicate entries are
    /// dropped. The returned list is a copy: assign it back to persist changes.
    /// </summary>
    public List<ByokProvider> ByokProviders
    {
        get
        {
            lock (_gate)
            {
                return [.. Providers().Select(p => Clone(p))];
            }
        }
        set
        {
            var clean = Normalize(value.Select(p => Clone(p)));
            lock (_gate)
            {
                if (SerializeProviders(clean, protect: false) == SerializeProviders(Providers(), protect: false))
                {
                    return;
                }

                var source = SerializeProviders(clean, protect: true);
                if (!Write(ProvidersKey, source)) return;
                _providerSource = source;
                _providers = clean;
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

        lock (_gate)
        {
            var found = Providers().FirstOrDefault(p => string.Equals(p.Key, wanted, StringComparison.OrdinalIgnoreCase));
            return found is null ? null : Clone(found);
        }
    }

    /// <summary>The chord stored for a shortcut action ("shortcut.&lt;id&gt;"), or null while it keeps its default.</summary>
    public string? GetShortcut(string id) =>
        Read("shortcut." + id, "") is { Length: > 0 } stored ? stored : null;

    /// <summary>Stores a shortcut override; null drops it so the action follows its default again.</summary>
    public void SetShortcut(string id, string? chord) => Save("shortcut." + id, chord);

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

    private string Read(string name, string fallback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            return CurrentValue(name) is { Length: > 0 } value ? value : fallback;
        }
    }

    private string? CurrentValue(string name) => _database.UseRecords(db => db.Open<Setting>().Key(name).Get()?.Value);

    private T Read<T>(string name, T fallback, Func<string, T> parse)
    {
        var raw = Read(name, "");
        if (raw.Length == 0) return fallback;
        try { return parse(raw); }
        catch (Exception error) when (error is FormatException or OverflowException) { return fallback; }
    }

    private void Save(string name, string? value)
    {
        lock (_gate)
        {
            if (!Write(name, value)) return;
        }
        OnChanged();
    }

    private bool Write(string name, string? value)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        return _database.UseRecords(db =>
        {
            var key = db.Open<Setting>().Key(name);
            if (key.Get()?.Value == value) return false;
            if (value is null) key.Delete();
            else key.Value(new(value));
            _announced[name] = value;
            return true;
        });
    }

    private void OnRecordChanged(RecordEntry<Setting> record, RecordAction _)
    {
        lock (_gate)
        {
            if (_stopped) return;
            // A local write already notified its listeners. Coalesce queued events to the current value.
            var current = CurrentValue(record.Key);
            if (_announced.TryGetValue(record.Key, out var last) && current == last) return;
            _announced[record.Key] = current;
        }
        OnChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _watch.Dispose();
            _providers.Clear();
            _providerSource = null;
            _announced.Clear();
            Changed = null;
            _dispatch = null;
            _hasThreadAccess = null;
        }
        _ownedDatabase?.Dispose();
    }
}
