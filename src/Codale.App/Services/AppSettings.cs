using System.Globalization;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Storage;

using Microsoft.UI.Dispatching;

namespace Codale.App.Services;

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
public static class AppSettings
{
    /// <summary>Editor font size bounds; matches the editor's own Ctrl+wheel zoom range.</summary>
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    /// <summary>Raised after any setting was written. Args are always <see cref="EventArgs.Empty"/>.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Records the calling thread as the UI thread so <see cref="Changed"/> is posted back to it
    /// when a setting turns on another one (a file-watcher callback, a background job).
    /// </summary>
    public static void AttachUiThread() => _uiQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>Values are written with the invariant culture, so they parse back the same on any locale.</summary>
    private static double ParseInvariant(string raw) => double.Parse(raw, CultureInfo.InvariantCulture);

    private static int ParseInt(string raw) => int.Parse(raw, CultureInfo.InvariantCulture);

    private static string FormatSize(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatText(string value) => value;

    private static string FormatFlag(bool value) => value ? "1" : "0";

    // Parsed values by settings key, guarded by FileLock (the file watcher clears them
    // from another thread). A hit is already validated and clamped.
    private static readonly Dictionary<string, object> Cache = new(StringComparer.Ordinal);

    private static T Get<T>(string key, Func<T> load) where T : notnull
    {
        lock (FileLock)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                return (T)hit;
            }

            var value = load();
            Cache[key] = value;
            return value;
        }
    }

    /// <summary>Persists <paramref name="value"/> (already clamped by the caller), refreshes the cache and tells listeners - unless it is already the current value.</summary>
    private static void Set<T>(string key, T value, Func<T, string> format) where T : notnull
    {
        lock (FileLock)
        {
            if (Cache.TryGetValue(key, out var cached) && EqualityComparer<T>.Default.Equals((T)cached, value))
            {
                return;
            }

            Write(key, format(value));
            Cache[key] = value;
        }

        OnChanged();
    }

    private static bool IsTheme(string raw) => raw is "System" or "Dark" or "Light";

    private static bool IsShell(string raw) => raw is "auto" or "pwsh" or "powershell" or "cmd";

    /// <summary>
    /// The transcript's base text size; everything in the chat scales from it. 12.5 is
    /// the built-in look, so that value means "no change".
    /// </summary>
    public static double ChatFontSize
    {
        get => Get("chat.fontSize", () => Read("chat.fontSize", 12.5d, ParseInvariant));
        set => Set("chat.fontSize", Math.Clamp(value, 9, 24), FormatSize);
    }

    /// <summary>The typeface the transcript reads in; code runs stay monospaced.</summary>
    public static string ChatFontFamily
    {
        get => Get("chat.fontFamily", () => Read("chat.fontFamily", "Segoe UI"));
        set => Set("chat.fontFamily", string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim(), FormatText);
    }

    /// <summary>The app theme: "System" follows the Windows app mode, else "Dark" or "Light".</summary>
    public static string Theme
    {
        get => Get("app.theme", () => Read("app.theme", "Dark") is var raw && IsTheme(raw) ? raw : "Dark");
        set => Set("app.theme", value is "System" or "Light" ? value : "Dark", FormatText);
    }

    /// <summary>The font size a new editor tab opens with.</summary>
    public static double EditorFontSize
    {
        get => Get("editor.fontSize", () => Read("editor.fontSize", 13d, ParseInvariant));
        set => Set("editor.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>Spaces a Tab keypress inserts; existing text keeps its tabs.</summary>
    public static int EditorTabWidth
    {
        get => Get("editor.tabWidth", () => Read("editor.tabWidth", 4, ParseInt));
        set => Set("editor.tabWidth", Math.Clamp(value, 1, 16), FormatInt);
    }

    /// <summary>Whether a shell command that trips none of the risk rules runs without asking (Plan mode aside).</summary>
    public static bool AutoApproveSafeShell
    {
        get => Get("shell.autoApproveSafe", () => ReadFlag("shell.autoApproveSafe", true));
        set => Set("shell.autoApproveSafe", value, FormatFlag);
    }

    /// <summary>Whether Claude sessions predict the next prompt and show it in the composer. Read when a session starts.</summary>
    public static bool SuggestNextPrompt
    {
        get => Get("chat.suggestNextPrompt", () => ReadFlag("chat.suggestNextPrompt", true));
        set => Set("chat.suggestNextPrompt", value, FormatFlag);
    }

    /// <summary>
    /// Whether an idle chat compacts itself just before its prompt cache expires. Compacting
    /// while the cache is warm reads the history at the cache-hit price; once it expires the
    /// next prompt re-writes the whole history to the cache instead.
    /// </summary>
    public static bool IdleCompact
    {
        get => Get("tokens.idleCompact", () => ReadFlag("tokens.idleCompact", true));
        set => Set("tokens.idleCompact", value, FormatFlag);
    }

    /// <summary>Idle seconds before that compaction: just under the API's 5-minute cache lifetime.</summary>
    public static int IdleCompactSeconds
    {
        get => Get("tokens.idleCompactSeconds", () => Read("tokens.idleCompactSeconds", 240, ParseInt));
        set => Set("tokens.idleCompactSeconds", Math.Clamp(value, 60, 3600), FormatInt);
    }

    /// <summary>Context below this many tokens is not worth a summary: compacting it would save less than it costs.</summary>
    public static int IdleCompactMinTokens
    {
        get => Get("tokens.idleCompactMinTokens", () => Read("tokens.idleCompactMinTokens", 50_000, ParseInt));
        set => Set("tokens.idleCompactMinTokens", Math.Max(0, value), FormatInt);
    }

    /// <summary>
    /// Whether chats keep a history per feature of the project: each message is routed to a feature
    /// (or to none, when it is no project work), and a session starts from that feature's summary.
    /// </summary>
    public static bool SmartHistory
    {
        get => Get("chat.smartHistory", () => ReadFlag("chat.smartHistory", true));
        set => Set("chat.smartHistory", value, FormatFlag);
    }

    /// <summary>Whether the editor draws the gutter with line numbers.</summary>
    public static bool EditorLineNumbers
    {
        get => Get("editor.lineNumbers", () => ReadFlag("editor.lineNumbers", true));
        set => Set("editor.lineNumbers", value, FormatFlag);
    }

    /// <summary>Whether tokens colour the editor text, or everything draws plain.</summary>
    public static bool EditorHighlighting
    {
        get => Get("editor.highlighting", () => ReadFlag("editor.highlighting", true));
        set => Set("editor.highlighting", value, FormatFlag);
    }

    /// <summary>Whether long editor lines fold at the viewport edge instead of scrolling sideways.</summary>
    public static bool EditorWordWrap
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
    public static Dictionary<string, string> SyntaxOverrides
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
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        set => Set(SyntaxOverridesKey, JsonSerializer.Serialize(value), FormatText);
    }

    /// <summary>
    /// The shell new terminal tabs run: "auto" (pwsh, then Windows PowerShell, then
    /// cmd), or a named choice that falls back to the automatic probe when missing.
    /// </summary>
    public static string TerminalShell
    {
        get => Get("terminal.shell", () => Read("terminal.shell", "auto") is var raw && IsShell(raw) ? raw : "auto");
        set => Set("terminal.shell", IsShell(value) ? value : "auto", FormatText);
    }

    /// <summary>
    /// The terminal's monospace family. Readers validate it against the installed
    /// fonts and fall back to Cascadia Mono, so a missing family cannot silently
    /// degrade to a proportional one and break the cell grid.
    /// </summary>
    public static string TerminalFontFamily
    {
        get => Get("terminal.fontFamily", () => Read("terminal.fontFamily", "Cascadia Mono"));
        set => Set("terminal.fontFamily", string.IsNullOrWhiteSpace(value) ? "Cascadia Mono" : value.Trim(), FormatText);
    }

    /// <summary>The font size a new terminal tab opens with (live change refits open ones).</summary>
    public static double TerminalFontSize
    {
        get => Get("terminal.fontSize", () => Read("terminal.fontSize", 13d, ParseInvariant));
        set => Set("terminal.fontSize", Math.Clamp(value, MinFontSize, MaxFontSize), FormatSize);
    }

    /// <summary>How many scrolled-off lines a terminal keeps, applied at session start.</summary>
    public static int TerminalScrollback
    {
        get => Get("terminal.scrollback", () => Read("terminal.scrollback", 10_000, ParseInt));
        set => Set("terminal.scrollback", Math.Clamp(value, 100, 1_000_000), FormatInt);
    }

    /// <summary>
    /// The endpoint background jobs call, or null to use a CLI: Settings picks Claude or
    /// Codex, or the picked provider lacks a base URL, key and flash model. Only the flash model
    /// is used - these jobs are short, and latency and cost are what the user feels.
    /// </summary>
    public static (string BaseUrl, string ApiKey, string Model)? HelperApiEndpoint =>
        HelperApiProvider is { } found ? found.Endpoint : null;

    /// <summary>
    /// What background jobs run on: "" for the Claude CLI, "Codex" for the Codex CLI,
    /// else the name of a BYOK provider. Never picked, it is Claude.
    /// Independent of the status bar's agent provider picker.
    /// </summary>
    public static string HelperProvider
    {
        get
        {
            var stored = Read("helper.provider", "").Trim();

            // Never picked is Off: background tasks only run once the user chooses a provider.
            if (stored.Length == 0 || string.Equals(stored, OffHelperProvider, StringComparison.OrdinalIgnoreCase))
            {
                return OffHelperProvider;
            }

            if (string.Equals(stored, ClaudeHelperProvider, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // A "Local" pick from a build that ran a model in-process names no provider now; it falls back to Claude.
            return string.Equals(stored, "Local", StringComparison.OrdinalIgnoreCase) ? "" : stored;
        }
        set
        {
            // Claude is stored by name: an empty value would read back as "never picked".
            var clean = value?.Trim() ?? "";
            var stored = clean.Length == 0 ? ClaudeHelperProvider : clean;
            if (string.Equals(Read("helper.provider", "").Trim(), stored, StringComparison.Ordinal))
            {
                return;
            }

            Write("helper.provider", stored);
            OnChanged();
        }
    }

    /// <summary>What <see cref="HelperProvider"/> stores for an explicit pick of the Claude CLI.</summary>
    private const string ClaudeHelperProvider = "Claude";

    /// <summary>What <see cref="HelperProvider"/> reads for Off, the default: no background task runs.</summary>
    public const string OffHelperProvider = "Off";

    public static bool HelperEnabled => !string.Equals(HelperProvider, OffHelperProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The BYOK provider background jobs call: only the one named by <see cref="HelperProvider"/>,
    /// and only when it has a base URL, key and flash model. Null otherwise - Claude or Codex was
    /// picked, or the provider is incomplete - and jobs run on the CLI. Never picks a provider
    /// on its own, and never follows the agent provider picked in the status bar.
    /// </summary>
    public static (ByokProvider Provider, (string BaseUrl, string ApiKey, string Model) Endpoint)? HelperApiProvider
    {
        get
        {
            var wanted = HelperProvider;
            if (wanted.Length == 0 || !HelperEnabled)
            {
                return null;
            }

            foreach (var provider in ByokProviders.Where(p => string.Equals(p.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                // Background jobs only ever run the flash model: a provider with only a pro model is not set up for them.
                var model = provider.FlashModel;
                if (!string.IsNullOrWhiteSpace(provider.BaseUrl) && !string.IsNullOrWhiteSpace(provider.ApiKey) && !string.IsNullOrWhiteSpace(model))
                {
                    return (provider, (provider.BaseUrl.Trim(), provider.ApiKey.Trim(), model.Trim()));
                }
            }

            return null;
        }
    }

    private static bool ReadFlag(string name, bool fallback) => Read(name, fallback ? "1" : "0") == "1";

    private static bool IsChatMode(string? mode) =>
        mode is "automatic" or "ask" or "plan" or "manual" or "acceptEdits" or "auto";

    /// <summary>The permission mode a fresh chat starts in: "automatic" (Auto), "ask", "plan", "manual", "acceptEdits" or "auto" (Full). Unset (or the retired "CLI default", stored as "") is Auto: a CLI left in its own default asks about every edit.</summary>
    public static string DefaultChatMode
    {
        get => Get("chat.defaultMode", () => Read("chat.defaultMode", "") is var raw && IsChatMode(raw) ? raw : "automatic");
        set => Set("chat.defaultMode", IsChatMode(value) ? value : "automatic", FormatText);
    }

    /// <summary>The last model/effort/mode the user picked for a CLI ("claude" or "codex"), saved as
    /// the default its new chats start with. An empty stored value (or null) means unset: the CLI's
    /// own default. Model and effort ids are CLI-specific, so the defaults are kept per CLI.</summary>
    private static string DefaultsKey(string cli, string leaf) => $"chat.defaults.{cli}.{leaf}";

    private static string? ReadDefault(string cli, string leaf) =>
        Get(DefaultsKey(cli, leaf), () => Read(DefaultsKey(cli, leaf), "")) is { Length: > 0 } stored ? stored : null;

    private static void WriteDefault(string cli, string leaf, string? value)
    {
        var key = DefaultsKey(cli, leaf);
        var raw = value is { Length: > 0 } ? value : "";
        lock (FileLock)
        {
            if (Read(key, "") == raw)
            {
                return;
            }

            Write(key, raw);
            // ReadDefault answers from the cache, so it must see the new value too.
            Cache[key] = raw;
        }

        OnChanged();
    }

    /// <summary>The model tier a fresh chat starts with: "cli", "lite", "flash", "pro" or "plus". Null means "cli".</summary>
    private static bool IsTier(string? tier) => tier is "cli" or "lite" or "flash" or "pro" or "plus";

    public static string DefaultTierFor(string cli) =>
        ReadDefault(cli, "tier") is { } saved && IsTier(saved) ? saved : "cli";

    public static void SetDefaultTier(string cli, string? tier) => WriteDefault(cli, "tier", IsTier(tier) ? tier : "");

    /// <summary>Which CLI model plays the "flash" (fast) or "pro" (capable) tier. Null means
    /// the app guesses from the CLI's catalogue; the tier's effort is fixed per tier.</summary>
    public static string? TierModelFor(string cli, string tier) => ReadDefault(cli, tier);

    public static void SetTierModel(string cli, string tier, string? model) => WriteDefault(cli, tier, model);

    /// <summary>The provider new chats of a CLI connect through: a name from <see cref="ByokProviders"/>, or "" for the CLI's own login.</summary>
    public static string CliProviderFor(string cli) =>
        ReadDefault(cli, "provider") is { } name && FindByok(name) is not null ? name : "";

    public static void SetCliProvider(string cli, string? name) => WriteDefault(cli, "provider", name?.Trim());

    /// <summary>The saved mode for a CLI, falling back to the global <see cref="DefaultChatMode"/> when unset.</summary>
    public static string DefaultModeFor(string cli) =>
        ReadDefault(cli, "mode") is { } saved && IsChatMode(saved) ? saved : DefaultChatMode;

    public static void SetDefaultMode(string cli, string? value) => WriteDefault(cli, "mode", IsChatMode(value) ? value : "");

    private static string ClaudeWindowKey(string model) => $"claude.contextWindow.{ClaudeContextProbe.ModelKey(model)}";

    /// <summary>
    /// The context window last seen for a Claude model, or null. A terminal CLI's transcript
    /// never says (its 1M pick is logged under the bare id), so the terminal panel measures
    /// against what a chat's CLI reported, or what an earlier session proved by passing 200K.
    /// </summary>
    public static int? ClaudeContextWindowFor(string model) =>
        Get(ClaudeWindowKey(model), () => Read(ClaudeWindowKey(model), 0, ParseInt)) is var window and > 0 ? window : null;

    public static void RememberClaudeContextWindow(string model, int window)
    {
        if (model.Length > 0 && window > 0)
        {
            Set(ClaudeWindowKey(model), window, FormatInt);
        }
    }

    /// <summary>Seconds between the git panel's working-tree checks.</summary>
    public static int GitPollSeconds
    {
        get => Get("git.pollSeconds", () => Read("git.pollSeconds", 5, ParseInt));
        set => Set("git.pollSeconds", Math.Clamp(value, 1, 600), FormatInt);
    }

    private const string ProvidersKey = "byok.providers";

    private static readonly JsonSerializerOptions ProviderJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static ByokProvider Clone(ByokProvider p, string? apiKey = null) => new()
    {
        Name = p.Name,
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
    private static string SerializeProviders(IEnumerable<ByokProvider> providers, bool protect) =>
        JsonSerializer.Serialize(
            providers.Select(p => Clone(p, protect ? SecretProtector.Protect(p.ApiKey) : p.ApiKey)).ToList(),
            ProviderJson);

    /// <summary>Drops nameless and duplicate (by trimmed name) entries.</summary>
    private static List<ByokProvider> Normalize(IEnumerable<ByokProvider> providers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return providers.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Name.Trim())).ToList();
    }

    /// <summary>The cached providers (decrypted); callers copy before handing them out. Call under <see cref="FileLock"/>.</summary>
    private static List<ByokProvider> Providers() => Get(ProvidersKey, LoadProviders);

    private static List<ByokProvider> LoadProviders()
    {
        var parsed = ParseProviders(Read(ProvidersKey, "[]"), out var hadPlaintextKey);
        if (hadPlaintextKey)
        {
            // A key typed or migrated in the clear: wrap it now rather than at the next edit.
            Write(ProvidersKey, SerializeProviders(parsed, protect: true));
        }

        return parsed;
    }

    /// <summary>
    /// The BYOK providers listed in settings.json. Nameless or duplicate entries are
    /// dropped. The returned list is a copy: assign it back to persist changes.
    /// </summary>
    public static List<ByokProvider> ByokProviders
    {
        get
        {
            lock (FileLock)
            {
                return Providers().Select(p => Clone(p)).ToList();
            }
        }
        set
        {
            var clean = Normalize(value.Select(p => Clone(p)));
            lock (FileLock)
            {
                if (SerializeProviders(clean, protect: false) == SerializeProviders(Providers(), protect: false))
                {
                    return;
                }

                Write(ProvidersKey, SerializeProviders(clean, protect: true));
                Cache[ProvidersKey] = clean;
            }

            OnChanged();
        }
    }

    private static List<ByokProvider> ParseProviders(string json, out bool hadPlaintextKey)
    {
        hadPlaintextKey = false;
        try
        {
            var parsed = Normalize(JsonSerializer.Deserialize<List<ByokProvider>>(json, ProviderJson) ?? []);
            foreach (var provider in parsed)
            {
                var stored = provider.ApiKey ?? "";
                hadPlaintextKey |= stored.Length > 0 && !SecretProtector.IsProtected(stored);
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
    public static ByokProvider? FindByok(string? name)
    {
        var wanted = name?.Trim() ?? "";
        if (wanted.Length == 0)
        {
            return null;
        }

        lock (FileLock)
        {
            var found = Providers().FirstOrDefault(p => string.Equals(p.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            return found is null ? null : Clone(found);
        }
    }

    /// <summary>The chord stored for a shortcut action ("shortcut.&lt;id&gt;"), or null while it keeps its default.</summary>
    public static string? GetShortcut(string id) =>
        Read("shortcut." + id, "") is { Length: > 0 } stored ? stored : null;

    /// <summary>Stores a shortcut override; null drops it so the action follows its default again.</summary>
    public static void SetShortcut(string id, string? chord)
    {
        var key = "shortcut." + id;
        lock (FileLock)
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
    private static void OnChanged()
    {
        if (_uiQueue is { HasThreadAccess: false } ui)
        {
            ui.TryEnqueue(() => Changed?.Invoke(null, EventArgs.Empty));
        }
        else
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The hand-editable settings file, next to the database:
    /// <c>%LOCALAPPDATA%\Codale\settings.json</c> (redirected into the package folder under MSIX).
    /// </summary>
    private static string FilePath { get; } = Path.Combine(
        Path.GetDirectoryName(CodaleStore.DefaultDatabasePath)!,
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Keys whose JSON value is a boolean; everything else numeric is written as a number, the rest as strings.</summary>
    private static readonly HashSet<string> BoolKeys =
    [
        "editor.lineNumbers", "editor.highlighting", "editor.wordWrap", "chat.suggestNextPrompt",
        "tokens.compressShell", "tokens.digest", "tokens.explore", "tokens.readGuard", "tokens.handoffNudge", "tokens.terse",
        "tokens.idleCompact",
    ];

    private static readonly HashSet<string> NumberKeys =
    [
        "editor.fontSize", "editor.tabWidth", "terminal.fontSize", "terminal.scrollback", "chat.fontSize", "git.pollSeconds",
        "tokens.autoCompactPercent", "tokens.idleCompactSeconds", "tokens.idleCompactMinTokens",
    ];

    private static readonly object FileLock = new();
    private static SortedDictionary<string, string>? _values;
    private static string _lastWritten = "";
    private static DispatcherQueue? _uiQueue;
    private static FileSystemWatcher? _watcher;

    private static SortedDictionary<string, string> Values
    {
        get
        {
            lock (FileLock)
            {
                if (_values is null)
                {
                    _uiQueue ??= DispatcherQueue.GetForCurrentThread();

                    _values = LoadFile() ?? SeedFromDatabase();
                    StartWatching();
                }

                return _values;
            }
        }
    }

    /// <summary>Parses the file; null when it is missing or unreadable (defaults stand for a broken file).</summary>
    private static SortedDictionary<string, string>? LoadFile()
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
                        var str = property.Value.GetString() ?? "";

                        // A hand-typed "true"/"false" for a toggle means what it says.
                        result[property.Name] = BoolKeys.Contains(property.Name) && bool.TryParse(str, out var flag)
                            ? (flag ? "1" : "0")
                            : str;
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
            if (_values is null)
            {
                // The next write would replace the file with defaults; keep the broken text recoverable.
                try
                {
                    File.Copy(FilePath, FilePath + ".broken", overwrite: true);
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }

            return _values ?? new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>First run with the file: carry over what the old database-backed settings held.</summary>
    private static SortedDictionary<string, string> SeedFromDatabase()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var providers = new List<ByokProvider>();
        try
        {
            using var store = new CodaleStore(CodaleStore.DefaultDatabasePath);
            foreach (var key in BoolKeys.Concat(NumberKeys).Concat(
                ["app.theme", "terminal.shell", "terminal.fontFamily", "chat.fontFamily", "chat.defaultMode", "chat.defaultModel", "chat.defaultEffort"]))
            {
                if (store.GetSetting(key) is { Length: > 0 } raw)
                {
                    result[key] = raw;
                }
            }

            // The single endpoint the old status-bar flyout held becomes the first provider.
            if (store.GetSetting("endpoint.baseUrl") is { Length: > 0 } legacyUrl)
            {
                providers.Add(new ByokProvider
                {
                    Name = "Custom endpoint",
                    BaseUrl = legacyUrl,
                    ApiKey = store.GetSetting("endpoint.apiKey") ?? "",
                    FlashModel = store.GetSetting("endpoint.model") ?? "",
                    ProModel = store.GetSetting("endpoint.smartModel") ?? "",
                });
            }
        }
        catch (StoreSchemaException ex)
        {
            // A database from a newer Codale: leave it alone and start from defaults.
            CrashLog.Warn("settings", $"legacy settings not carried over: {ex.Message}");
        }
        catch (Exception)
        {
            // No database yet: start from defaults.
        }

        // Spell out every key so the freshly created file shows what can be configured.
        foreach (var (key, value) in new[]
        {
            ("app.theme", "Dark"), ("editor.fontSize", "13"), ("editor.tabWidth", "4"), ("editor.lineNumbers", "1"),
            ("editor.highlighting", "1"), ("editor.wordWrap", "0"), ("terminal.shell", "auto"),
            ("terminal.fontFamily", "Cascadia Mono"), ("terminal.fontSize", "13"), ("terminal.scrollback", "10000"),
            ("chat.fontSize", "12.5"), ("chat.fontFamily", "Segoe UI"),
            ("chat.defaultMode", " "), ("chat.defaultModel", " "), ("chat.defaultEffort", " "),
            ("git.pollSeconds", "5"),
        })
        {
            result.TryAdd(key, value);
        }

        result["chat.defaultMode"] = result["chat.defaultMode"].Trim();
        result["chat.defaultModel"] = result["chat.defaultModel"].Trim();
        result["chat.defaultEffort"] = result["chat.defaultEffort"].Trim();

        result[ProvidersKey] = SerializeProviders(providers, protect: true);
        Persist(result);
        return result;
    }

    private static void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Editors write in bursts; each event pushes the single reload back by 150 ms.
            var debounce = new System.Threading.Timer(_ => OnFileEdited(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => debounce.Change(150, Timeout.Infinite);
            _watcher.Created += (_, _) => debounce.Change(150, Timeout.Infinite);
            _watcher.Renamed += (_, _) => debounce.Change(150, Timeout.Infinite);
        }
        catch (Exception)
        {
            // No live reload; edits apply on next start.
        }
    }

    /// <summary>A hand edit landed: reload, drop the caches and tell open windows.</summary>
    private static void OnFileEdited()
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

        lock (FileLock)
        {
            if (text == _lastWritten)
            {
                return; // our own write
            }

            var fresh = LoadFile();
            if (fresh is null)
            {
                return;
            }

            _values = fresh;
            Cache.Clear();
        }

        OnChanged();
    }

    private static string Read(string name, string fallback)
    {
        // The file watcher swaps and Write mutates the dictionary from other threads.
        lock (FileLock)
        {
            return Values.TryGetValue(name, out var raw) && raw.Length > 0 ? raw : fallback;
        }
    }

    private static T Read<T>(string name, T fallback, Func<string, T> parse)
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

    private static void Write(string name, string value)
    {
        lock (FileLock)
        {
            var values = Values;
            values[name] = value;
            Persist(values);
        }
    }

    /// <summary>Writes the file through a temp file and a replace, so a crash mid-write never leaves half a settings.json. Failures are logged; the in-memory values stand.</summary>
    private static void Persist(SortedDictionary<string, string> values)
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
            CrashLog.Warn("settings", $"could not save {FilePath}: {ex.Message}");
        }
    }

    /// <summary>Renders the values as typed JSON: booleans and numbers as such, the rest as strings.</summary>
    private static string Serialize(SortedDictionary<string, string> values)
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

            root[key] = BoolKeys.Contains(key) ? raw == "1"
                : NumberKeys.Contains(key) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
                : raw;
        }

        return JsonSerializer.Serialize(root, JsonOptions);
    }
}