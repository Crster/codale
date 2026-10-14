using Codale.Agents.Claude;
using Codale.Agents;
using Codale.Agents.OpenAi;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

using Windows.System;

namespace Codale.App.Controls;

/// <summary>
/// The app Settings pane: the app-level preferences (<see cref="AppSettings"/>) in
/// one place - editor and terminal typography, the default shell, chat defaults and
/// the git refresh pace. It slides in over the right edge of the main window. Every
/// change is written through at once and applied live to the open project windows
/// via <see cref="AppSettings.Changed"/>.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    /// <summary>Populating the controls must not write the values straight back out.</summary>
    private bool _loading = true;

    /// <summary>The providers being edited; every change is written straight back to settings.json.</summary>
    private readonly List<ByokProvider> _providers = [];

    /// <summary>The user asked to dismiss the pane (close button).</summary>
    public event EventHandler? CloseRequested;

    public SettingsPanel()
    {
        InitializeComponent();

        // Only listen while in the visual tree: the settings event is static and windows come and go.
        Loaded += (_, _) => AppSettings.Changed += OnAppSettingsChanged;
        Unloaded += (_, _) => AppSettings.Changed -= OnAppSettingsChanged;

        Reload();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        ReloadProvidersIfEditedElsewhere();
        RefreshShortcutRows();
    }

    /// <summary>Re-reads every setting into the controls; another window may have changed them since.</summary>
    public void Reload()
    {
        _loading = true;

        EditorFontSizeBox.Value = AppSettings.EditorFontSize;
        EditorTabWidthBox.SelectedIndex = AppSettings.EditorTabWidth switch
        {
            2 => 0,
            8 => 2,
            _ => 1,
        };
        EditorLineNumbersToggle.IsOn = AppSettings.EditorLineNumbers;
        EditorHighlightingToggle.IsOn = AppSettings.EditorHighlighting;
        RefreshSyntaxSection();
        WordWrapToggle.IsOn = AppSettings.EditorWordWrap;
        SuggestNextPromptToggle.IsOn = AppSettings.SuggestNextPrompt;
        CompressShellToggle.IsOn = AppSettings.TokenSaverCompressShell;
        DigestToggle.IsOn = AppSettings.TokenSaverDigest;
        ExploreToggle.IsOn = AppSettings.TokenSaverExplore;
        ReadGuardToggle.IsOn = AppSettings.TokenSaverReadGuard;
        HandoffNudgeToggle.IsOn = AppSettings.TokenSaverHandoffNudge;
        CheapSubagentsToggle.IsOn = AppSettings.TokenSaverCheapSubagents;
        TerseToggle.IsOn = AppSettings.TokenSaverTerse;
        AutoCompactBox.Value = AppSettings.TokenSaverAutoCompactPercent;
        DenyRulesBox.Text = string.Join("\r", AppSettings.TokenSaverDenyRules);

        ThemeBox.SelectedIndex = AppSettings.Theme switch
        {
            "Light" => 1,
            "System" => 2,
            _ => 0,
        };

        TerminalFontFamilyBox.SelectedIndex = IndexOfFontFamily(AppSettings.TerminalFontFamily);
        TerminalShellBox.SelectedIndex = AppSettings.TerminalShell switch
        {
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 0,
        };
        TerminalFontSizeBox.Value = AppSettings.TerminalFontSize;
        TerminalScrollbackBox.SelectedIndex = AppSettings.TerminalScrollback switch
        {
            <= 1_000 => 0,
            <= 10_000 => 1,
            <= 50_000 => 2,
            _ => 3,
        };

        DefaultChatModeBox.SelectedIndex = Math.Max(0, DefaultChatModeBox.Items.ToList().FindIndex(
            i => (i as ComboBoxItem)?.Tag as string == AppSettings.DefaultChatMode));

        DefaultTierBox.SelectedIndex = Math.Max(0, DefaultTierBox.Items.ToList().FindIndex(
            i => (i as ComboBoxItem)?.Tag as string == AppSettings.DefaultTierFor("claude")));

        BuildTierModelRows();

        ChatFontSizeBox.Value = AppSettings.ChatFontSize;
        PopulateChatFontFamilies();

        GitPollBox.Value = AppSettings.GitPollSeconds;

        if (_shortcutRows.Count == 0)
        {
            BuildShortcutRows();
        }

        EndRecording();
        RefreshShortcutRows();

        _providers.Clear();
        _providers.AddRange(AppSettings.ByokProviders);
        BuildProviderEditors();

        _loading = false;
    }

    private const string ShortcutHelp = "Click a shortcut, then press the new keys. Backspace clears it, Esc cancels (for Stop chat, Esc binds Esc Esc).";

    private readonly Dictionary<string, (Button Chord, Button Reset)> _shortcutRows = [];

    /// <summary>The action whose chord is waiting for a key press; null when nothing is being recorded.</summary>
    private string? _recordingId;

    private void BuildShortcutRows()
    {
        ShortcutsPanel.Children.Clear();
        _shortcutRows.Clear();

        var divider = (Style)ShortcutsPanel.Resources["ShortcutDivider"];
        var hint = (Style)ShortcutsPanel.Resources["ShortcutText"];

        foreach (var action in KeyboardShortcuts.Actions)
        {
            if (ShortcutsPanel.Children.Count > 0)
            {
                ShortcutsPanel.Children.Add(new Rectangle { Style = divider });
            }

            var row = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 24 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = action.Label });
            labels.Children.Add(new TextBlock { Text = action.Description, Style = hint });
            row.Children.Add(labels);

            var chord = new Button { Tag = action.Id, MinWidth = 150 };
            chord.Click += (_, _) => BeginRecording(action.Id);
            chord.KeyDown += OnShortcutRecorderKeyDown;
            chord.LostFocus += (_, _) =>
            {
                if (_recordingId == action.Id)
                {
                    EndRecording();
                }
            };

            var reset = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Padding = new Thickness(8, 6, 8, 6),
            };
            ToolTipService.SetToolTip(reset, "Reset to default");
            reset.Click += (_, _) =>
            {
                KeyboardShortcuts.Set(action.Id, action.Default);
                ShortcutHint.Text = ShortcutHelp;
                RefreshShortcutRows();
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            controls.Children.Add(chord);
            controls.Children.Add(reset);
            Grid.SetColumn(controls, 1);
            row.Children.Add(controls);

            ShortcutsPanel.Children.Add(row);
            _shortcutRows[action.Id] = (chord, reset);
        }

        ShortcutHint.Text = ShortcutHelp;
    }

    private void RefreshShortcutRows()
    {
        foreach (var action in KeyboardShortcuts.Actions)
        {
            if (!_shortcutRows.TryGetValue(action.Id, out var row))
            {
                continue;
            }

            var chord = KeyboardShortcuts.Get(action.Id);
            row.Chord.Content = _recordingId == action.Id ? "Press keys…" : chord.Length == 0 ? "Not set" : chord;
            row.Reset.IsEnabled = chord != action.Default;
        }
    }

    private void BeginRecording(string id)
    {
        EndRecording();
        _recordingId = id;
        KeyboardShortcuts.IsRecording = true;
        ShortcutHint.Text = ShortcutHelp;
        RefreshShortcutRows();
        _shortcutRows[id].Chord.Focus(FocusState.Programmatic);
    }

    private void EndRecording()
    {
        if (_recordingId is null)
        {
            return;
        }

        _recordingId = null;
        KeyboardShortcuts.IsRecording = false;
        ShortcutHint.Text = ShortcutHelp;
        RefreshShortcutRows();
    }

    /// <summary>Captures the chord while a shortcut is being recorded; anything else acts like a normal button press.</summary>
    private void OnShortcutRecorderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingId is not { } id || (sender as Button)?.Tag as string != id)
        {
            return;
        }

        // Handled before anything below: Esc must cancel here, not also close the pane.
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            // Stop chat is the one action Esc can be bound to: pressed twice.
            if (id == KeyboardShortcuts.StopChat)
            {
                KeyboardShortcuts.Set(id, KeyboardShortcuts.DoubleEscape);
            }

            EndRecording();
            return;
        }

        if (e.Key is VirtualKey.Back or VirtualKey.Delete && !KeyChord.AnyModifierDown())
        {
            KeyboardShortcuts.Set(id, "");
            EndRecording();
            return;
        }

        // A modifier on its own, or a key that cannot be named: keep waiting.
        if (KeyChord.FromKeyPress(e.Key) is not { } chord)
        {
            return;
        }

        if (!chord.IsUsable)
        {
            ShortcutHint.Text = "Hold Ctrl or Alt with the key (function keys work alone).";
            return;
        }

        if (KeyboardShortcuts.IsReserved(chord))
        {
            ShortcutHint.Text = $"{chord} is reserved for text editing. Try another.";
            return;
        }

        if (KeyboardShortcuts.Owner(chord, id) is { } owner)
        {
            ShortcutHint.Text = $"{chord} is already used by \"{owner.Label}\". Change that one first.";
            return;
        }

        KeyboardShortcuts.Set(id, chord.ToString());
        EndRecording();
    }

    private void OnResetShortcutsClick(object sender, RoutedEventArgs e)
    {
        EndRecording();
        foreach (var action in KeyboardShortcuts.Actions)
        {
            KeyboardShortcuts.Set(action.Id, action.Default);
        }

        RefreshShortcutRows();
    }

    private void BuildProviderEditors()
    {
        ProvidersPanel.Children.Clear();
        foreach (var provider in _providers)
        {
            ProvidersPanel.Children.Add(BuildProviderEditor(provider, expanded: false));
        }

        RefreshHelperProviderBox();
    }

    /// <summary>Rebuilds the background-task provider list: Claude, Codex, then each BYOK provider by name.</summary>
    private void RefreshHelperProviderBox()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var wanted = AppSettings.HelperProvider;
            HelperProviderBox.Items.Clear();
            HelperProviderBox.IsEnabled = true;

            HelperProviderBox.Items.Add("Claude");
            HelperProviderBox.Items.Add("Codex");

            var named = _providers.Where(p => p.Name.Trim().Length > 0 && !p.IsAccount).ToList();
            foreach (var provider in named)
            {
                HelperProviderBox.Items.Add(provider.Name.Trim());
            }

            var index = wanted.Length == 0
                ? 0
                : HelperProviderBox.Items.ToList().FindIndex(i => string.Equals(i as string, wanted, StringComparison.OrdinalIgnoreCase));
            HelperProviderBox.SelectedIndex = Math.Max(index, 0);
        }
        finally
        {
            _loading = wasLoading;
            RefreshTokenSaverHint();
            RefreshCodexAuthStatus();
        }
    }

    private void OnHelperProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && HelperProviderBox.SelectedIndex >= 0)
        {
            var selected = HelperProviderBox.SelectedItem as string ?? "";
            var name = string.Equals(selected, "Claude", StringComparison.OrdinalIgnoreCase) ? "" : selected;
            WriteSettings(() => AppSettings.HelperProvider = name);
            RefreshTokenSaverHint();
            RefreshCodexAuthStatus();
        }
    }

    private void RefreshCodexAuthStatus()
    {
        var isCodex = string.Equals(AppSettings.HelperProvider, "Codex", StringComparison.OrdinalIgnoreCase);
        CodexAuthRow.Visibility = isCodex ? Visibility.Visible : Visibility.Collapsed;
        if (!isCodex)
        {
            return;
        }

        CodexAuthStatusText.Text = "Checking login status...";
        _ = Task.Run(async () =>
        {
            var loggedIn = false;
            try
            {
                var codexPath = Codale.Agents.CliLocator.Find("codex");
                if (codexPath is null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        CodexAuthStatusText.Text = "Codex CLI is not installed.";
                        CodexSignInButton.IsEnabled = false;
                    });
                    return;
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = codexPath,
                    Arguments = "login status",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(startInfo);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync();
                    loggedIn = proc.ExitCode == 0;
                }
            }
            catch
            {
                loggedIn = false;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                CodexAuthStatusText.Text = loggedIn ? "Logged in to OpenAI Codex" : "Not logged in";
                CodexSignInButton.IsEnabled = true;
                CodexSignInButton.Content = loggedIn ? "Re-authenticate" : "Sign in";
            });
        });
    }

    private void OnCodexSignInClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k codex login")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            CrashLog.Error("settings", "Codex sign-in failed to start", ex);
        }
    }

    /// <summary>The model-backed token savers say why they are idle when background tasks have no custom provider.</summary>
    private void RefreshTokenSaverHint()
    {
        var hasApi = AppSettings.HasHelperApi;
        TokenSaverByokHint.Visibility = hasApi ? Visibility.Collapsed : Visibility.Visible;
        DigestToggle.IsEnabled = hasApi;
        ExploreToggle.IsEnabled = hasApi;
    }

    private void OnTokenSaverToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        WriteSettings(() =>
        {
            if (ReferenceEquals(toggle, CompressShellToggle)) AppSettings.TokenSaverCompressShell = on;
            else if (ReferenceEquals(toggle, DigestToggle)) AppSettings.TokenSaverDigest = on;
            else if (ReferenceEquals(toggle, ExploreToggle)) AppSettings.TokenSaverExplore = on;
            else if (ReferenceEquals(toggle, ReadGuardToggle)) AppSettings.TokenSaverReadGuard = on;
            else if (ReferenceEquals(toggle, HandoffNudgeToggle)) AppSettings.TokenSaverHandoffNudge = on;
            else if (ReferenceEquals(toggle, CheapSubagentsToggle)) AppSettings.TokenSaverCheapSubagents = on;
            else if (ReferenceEquals(toggle, TerseToggle)) AppSettings.TokenSaverTerse = on;
        });
    }

    private void OnAutoCompactChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        var percent = double.IsFinite(sender.Value) ? (int)Math.Round(sender.Value) : 0;
        WriteSettings(() => AppSettings.TokenSaverAutoCompactPercent = percent);
    }

    private void OnDenyRulesLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // A WinUI TextBox keeps line breaks as a bare \r.
        var rules = DenyRulesBox.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        WriteSettings(() => AppSettings.TokenSaverDenyRules = rules);
    }

    private Expander BuildProviderEditor(ByokProvider provider, bool expanded)
    {
        var header = new TextBlock { Text = provider.Name };
        var expander = new Expander
        {
            Header = header,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 10 };

        TextBox Field(string label, string placeholder, string value, Action<string> apply, Action? after = null)
        {
            var box = new TextBox { Header = label, PlaceholderText = placeholder, Text = value };
            box.TextChanged += (_, _) =>
            {
                apply(box.Text.Trim());
                SaveProviders();
                after?.Invoke();
            };
            panel.Children.Add(box);
            return box;
        }

        var previousName = provider.Name;
        Field("Name", "Shown in the status bar", provider.Name, v => provider.Name = v, () =>
        {
            header.Text = provider.Name;

            if (string.Equals(AppSettings.HelperProvider, previousName, StringComparison.OrdinalIgnoreCase) &&
                provider.Name.Length > 0)
            {
                WriteSettings(() => AppSettings.HelperProvider = provider.Name);
            }

            // A blank name is transient while retyping; keep matching on the last real one.
            if (provider.Name.Length > 0)
            {
                previousName = provider.Name;
            }
        });

        if (provider.IsAccount)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "A separate Claude CLI login. Sign in once, then pick it in the provider picker.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });

            var signIn = new Button { Content = "Sign in" };
            signIn.Click += (_, _) => SignInAccount(provider);
            var removeAccount = new Button { Content = "Remove account", HorizontalAlignment = HorizontalAlignment.Right };
            removeAccount.Click += (_, _) =>
            {
                _providers.Remove(provider);
                SaveProviders();
                BuildProviderEditors();
            };
            var buttons = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            buttons.Children.Add(signIn);
            Grid.SetColumn(removeAccount, 1);
            buttons.Children.Add(removeAccount);
            panel.Children.Add(buttons);

            expander.Content = panel;
            return expander;
        }

        Field("OpenAI-compatible base URL", "https://api.example.com/v1 (used by Codex CLI)", provider.BaseUrl, v => provider.BaseUrl = v);
        Field("Anthropic-compatible base URL", "https://api.example.com (used by Claude CLI; blank = OpenAI only)", provider.AnthropicBaseUrl, v => provider.AnthropicBaseUrl = v);

        var key = new PasswordBox { Header = "API key", Password = provider.ApiKey };
        key.PasswordChanged += (_, _) =>
        {
            provider.ApiKey = key.Password;
            SaveProviders();
        };
        panel.Children.Add(key);

        // Model pickers list what the endpoint's /models returns; typing an id still works.
        var modelBoxes = new List<ComboBox>();

        ComboBox ModelField(string label, string placeholder, string value, Action<string> apply)
        {
            var box = new ComboBox
            {
                Header = label,
                PlaceholderText = placeholder,
                IsEditable = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Text = value,
            };
            if (value.Length > 0)
            {
                box.Items.Add(value);
                box.SelectedIndex = 0;
            }

            box.TextSubmitted += (_, e) =>
            {
                apply(e.Text.Trim());
                SaveProviders();
            };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string id)
                {
                    apply(id);
                    SaveProviders();
                }
            };
            box.DropDownOpened += async (_, _) => await LoadModelsAsync(provider, modelBoxes);
            panel.Children.Add(box);
            modelBoxes.Add(box);
            return box;
        }

        ModelField("Flash model", "Select a model", provider.FlashModel, v => provider.FlashModel = v);
        ModelField("Pro model", "Select a model (optional)", provider.ProModel, v => provider.ProModel = v);

        var remove = new Button { Content = "Remove provider", HorizontalAlignment = HorizontalAlignment.Right };
        remove.Click += (_, _) =>
        {
            _providers.Remove(provider);
            if (string.Equals(AppSettings.HelperProvider, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                WriteSettings(() => AppSettings.HelperProvider = "");
            }

            SaveProviders();
            BuildProviderEditors();
        };
        panel.Children.Add(remove);

        expander.Content = panel;
        return expander;
    }

    private static readonly System.Net.Http.HttpClient ModelHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Model ids from an OpenAI-style list: <c>data</c>, <c>models</c>, <c>data.data</c> or a bare array; items are strings or objects with id/model/name.</summary>
    private static List<string> ReadModelIds(System.Text.Json.JsonElement root)
    {
        static System.Text.Json.JsonElement? FindArray(System.Text.Json.JsonElement e, int depth = 0)
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return e;
            }

            if (e.ValueKind != System.Text.Json.JsonValueKind.Object || depth > 2)
            {
                return null;
            }

            foreach (var name in new[] { "data", "models", "result" })
            {
                if (e.TryGetProperty(name, out var inner) && FindArray(inner, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        if (FindArray(root) is not { } array)
        {
            return [];
        }

        static string Id(System.Text.Json.JsonElement m)
        {
            if (m.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return m.GetString() ?? "";
            }

            foreach (var key in new[] { "id", "slug", "model", "name" })
            {
                if (m.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    m.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return v.GetString() ?? "";
                }
            }

            return "";
        }

        return array.EnumerateArray().Select(Id).Where(id => id.Length > 0).Distinct().Order().ToList();
    }

    /// <summary>Shows why the list could not load as a tooltip on the model boxes; null clears it.</summary>
    private static void SetModelStatus(List<ComboBox> boxes, string? message)
    {
        foreach (var box in boxes)
        {
            ToolTipService.SetToolTip(box, message);
        }
    }

    /// <summary>Fills the model dropdowns from the endpoint's OpenAI-style GET /models; failures leave the current list.</summary>
    private static async Task LoadModelsAsync(ByokProvider provider, List<ComboBox> boxes)
    {
        var anthropicOnly = string.IsNullOrWhiteSpace(provider.BaseUrl);
        if (anthropicOnly && string.IsNullOrWhiteSpace(provider.AnthropicBaseUrl))
        {
            return;
        }

        string url;
        if (anthropicOnly)
        {
            var root = provider.AnthropicBaseUrl.Trim().TrimEnd('/');
            url = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/models" : root + "/v1/models";
        }
        else
        {
            url = provider.BaseUrl.TrimEnd('/') + "/models";
        }

        List<string> ids;
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (provider.ApiKey.Length > 0)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
                if (anthropicOnly)
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
            }

            using var response = await ModelHttp.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            ids = ReadModelIds(doc.RootElement);
            if (ids.Count == 0)
            {
                // Some gateways answer 200 with an error envelope ({"code":401,"msg":...}).
                var detail = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : "no models in the response";
                SetModelStatus(boxes, $"Could not list models: {detail}");
                return;
            }
        }
        catch (Exception ex)
        {
            SetModelStatus(boxes, $"Could not list models: {ex.Message}");
            return;
        }

        SetModelStatus(boxes, null);

        foreach (var box in boxes)
        {
            var current = box.Text;
            box.Items.Clear();
            foreach (var id in ids)
            {
                box.Items.Add(id);
            }

            if (current.Length > 0)
            {
                var index = ids.IndexOf(current);
                if (index >= 0)
                {
                    box.SelectedIndex = index;
                }
                else
                {
                    box.Text = current;
                }
            }
        }
    }

    private void SaveProviders()
    {
        if (!_loading)
        {
            WriteSettings(() => AppSettings.ByokProviders = _providers);

            // The background-task list mirrors the providers: add, rename and remove show up at once.
            RefreshHelperProviderBox();
        }
    }

    /// <summary>True while this pane is the one writing settings, so its own writes do not reload the editors.</summary>
    private bool _selfWrite;

    private void WriteSettings(Action write)
    {
        _selfWrite = true;
        try
        {
            write();
        }
        finally
        {
            _selfWrite = false;
        }
    }

    /// <summary>A hand edit of settings.json changed the providers: pick it up instead of writing the stale list back on the next keystroke.</summary>
    private void ReloadProvidersIfEditedElsewhere()
    {
        if (_loading || _selfWrite)
        {
            return;
        }

        var fresh = AppSettings.ByokProviders;
        if (System.Text.Json.JsonSerializer.Serialize(fresh) == System.Text.Json.JsonSerializer.Serialize(_providers))
        {
            return;
        }

        _providers.Clear();
        _providers.AddRange(fresh);
        BuildProviderEditors();
    }

    private void OnAddProviderClick(object sender, RoutedEventArgs e)
    {
        var number = _providers.Count + 1;
        string name;
        do
        {
            name = $"Provider {number++}";
        }
        while (_providers.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));

        var provider = new ByokProvider { Name = name };
        _providers.Add(provider);
        SaveProviders();
        ProvidersPanel.Children.Add(BuildProviderEditor(provider, expanded: true));
    }

    private void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        var number = _providers.Count(p => p.IsAccount) + 1;
        string name;
        do
        {
            name = $"Default {number++}";
        }
        while (_providers.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));

        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "accounts");
        var dir = System.IO.Path.Combine(root,Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var account = new ByokProvider { Name = name, ConfigDir = dir };
        _providers.Add(account);
        SaveProviders();
        ProvidersPanel.Children.Add(BuildProviderEditor(account, expanded: true));
    }

    /// <summary>Opens a console running the Claude CLI's login against the account's own config directory.</summary>
    private static void SignInAccount(ByokProvider account)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k claude auth login")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            start.Environment["CLAUDE_CONFIG_DIR"] = account.ConfigDir;
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            CrashLog.Error("settings", "Account sign-in failed to start", ex);
        }
    }

    /// <summary>The chat reading fonts worth offering; a stored custom family stays selectable.</summary>
    private void PopulateChatFontFamilies()
    {
        ChatFontFamilyBox.Items.Clear();
        foreach (var family in new[] { "Segoe UI", "Segoe UI Variable", "Calibri", "Candara", "Corbel", "Georgia", "Verdana", "Cascadia Mono" })
        {
            ChatFontFamilyBox.Items.Add(new ComboBoxItem { Content = family });
        }

        var stored = AppSettings.ChatFontFamily;
        var index = -1;
        for (var i = 0; i < ChatFontFamilyBox.Items.Count; i++)
        {
            if (string.Equals((ChatFontFamilyBox.Items[i] as ComboBoxItem)?.Content as string, stored, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            ChatFontFamilyBox.Items.Add(new ComboBoxItem { Content = stored });
            index = ChatFontFamilyBox.Items.Count - 1;
        }

        ChatFontFamilyBox.SelectedIndex = index;
    }

    /// <summary>Finds the font-family entry matching the stored name, adding it when it is a custom family.</summary>
    private int IndexOfFontFamily(string stored)
    {
        for (var i = 0; i < TerminalFontFamilyBox.Items.Count; i++)
        {
            if (string.Equals((TerminalFontFamilyBox.Items[i] as ComboBoxItem)?.Content as string, stored, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        TerminalFontFamilyBox.Items.Add(new ComboBoxItem { Content = stored });
        return TerminalFontFamilyBox.Items.Count - 1;
    }

    private void OnEditorFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.EditorFontSize = sender.Value;
    }

    private void OnEditorTabWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.EditorTabWidth = EditorTabWidthBox.SelectedIndex switch
        {
            0 => 2,
            2 => 8,
            _ => 4,
        };
    }

    private void OnEditorLineNumbersToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.EditorLineNumbers = EditorLineNumbersToggle.IsOn;
        }
    }

    // ------------------------------------------------------------------ syntax

    private bool _syntaxSubscribed;

    /// <summary>Rebuilds the installed-languages list and the override count from the store and settings.</summary>
    private void RefreshSyntaxSection()
    {
        SyntaxSelection.Start();
        if (!_syntaxSubscribed)
        {
            // A grammar installed from the status bar (or by a download finishing) shows up here too.
            _syntaxSubscribed = true;
            SyntaxService.Store.Changed += () => DispatcherQueue.TryEnqueue(RefreshSyntaxSection);
        }

        var all = SyntaxService.Catalog.All;
        var installed = all.Where(l => !l.IsBuiltin).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SyntaxSummary.Text = $"{all.Count(l => l.IsBuiltin)} built-in languages · {installed.Count} installed";

        SyntaxList.Children.Clear();
        foreach (var language in installed)
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 32 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = language.Name });
            label.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Text = $"{string.Join(", ", language.Extensions.Select(e => "." + e))} · {language.Source.ToString().ToLowerInvariant()}",
            });
            row.Children.Add(label);

            var export = new Button { Content = "Export", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            export.Click += async (_, _) => await ExportGrammarAsync(language);
            Grid.SetColumn(export, 1);
            row.Children.Add(export);

            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => SyntaxService.Store.Remove(language.Id);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            SyntaxList.Children.Add(row);
        }

        if (installed.Count == 0)
        {
            SyntaxList.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Text = "None yet. Download, generate or import one above.",
            });
        }

        var overrides = AppSettings.SyntaxOverrides.Count;
        SyntaxOverridesHint.Text = overrides == 0
            ? "Files you set by hand from the status bar appear here."
            : $"{overrides} file{(overrides == 1 ? "" : "s")} set by hand from the status bar.";
        SyntaxClearOverridesButton.IsEnabled = overrides > 0;
    }

    private async Task ExportGrammarAsync(LanguageInfo language)
    {
        if (SyntaxService.Store.Find(language.Id) is not { } entry || SyntaxService.Store.ReadGrammar(entry) is not { } json)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{language.Id}.tmLanguage" };
        picker.FileTypeChoices.Add("TextMate grammar", [".json"]);
        if (App.Current.MainWindowHandle is { } hwnd)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        if (await picker.PickSaveFileAsync() is { } file)
        {
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }
    }

    private void OnSyntaxDownloadClick(object sender, RoutedEventArgs e) => _ = SyntaxDialogs.ShowCatalogAsync(XamlRoot);

    private async void OnSyntaxGenerateClick(object sender, RoutedEventArgs e)
    {
        // Settings has no file in front, so the dialog asks for the language and an optional sample.
        using var helper = new HelperModel(
            () => AppSettings.HelperApiEndpoint is { } api ? new OpenAiEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null,
            () => AppSettings.HelperProvider);
        await SyntaxDialogs.ShowGenerateAsync(XamlRoot, helper, "", "", "");
    }

    private void OnSyntaxImportClick(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindowHandle is { } hwnd)
        {
            _ = SyntaxDialogs.ImportAsync(XamlRoot, hwnd);
        }
    }

    private void OnSyntaxClearOverridesClick(object sender, RoutedEventArgs e)
    {
        AppSettings.SyntaxOverrides = new Dictionary<string, string>();

        // Open editors re-detect their language; the store event is the one nudge they already listen for.
        SyntaxService.Store.NotifyChanged();
    }

    private void OnSyntaxOpenFolderClick(object sender, RoutedEventArgs e)
    {
        SyntaxSelection.Start();
        Directory.CreateDirectory(SyntaxService.Store.Root);
        _ = Windows.System.Launcher.LaunchFolderPathAsync(SyntaxService.Store.Root);
    }

    private void OnEditorHighlightingToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.EditorHighlighting = EditorHighlightingToggle.IsOn;
        }
    }

    private void OnSuggestNextPromptToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.SuggestNextPrompt = SuggestNextPromptToggle.IsOn;
        }
    }

    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.EditorWordWrap = WordWrapToggle.IsOn;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.Theme = ThemeBox.SelectedIndex switch
            {
                1 => "Light",
                2 => "System",
                _ => "Dark",
            };
        }
    }

    private void OnTerminalFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.TerminalFontFamily =
                (TerminalFontFamilyBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Cascadia Mono";
        }
    }

    private void OnChatFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.ChatFontSize = sender.Value;
    }

    private void OnChatFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.ChatFontFamily =
                (ChatFontFamilyBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Segoe UI";
        }
    }

    private void OnTerminalShellChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.TerminalShell = TerminalShellBox.SelectedIndex switch
        {
            1 => "pwsh",
            2 => "powershell",
            3 => "cmd",
            _ => "auto",
        };
    }

    private void OnTerminalFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.TerminalFontSize = sender.Value;
    }

    private void OnTerminalScrollbackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.TerminalScrollback = TerminalScrollbackBox.SelectedIndex switch
        {
            0 => 1_000,
            2 => 50_000,
            3 => 100_000,
            _ => 10_000,
        };
    }

    private void OnDefaultChatModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.DefaultChatMode = (DefaultChatModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }
    }

    private void OnDefaultTierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // The tier is remembered per CLI; the settings row sets the starting point for both.
        var tier = (DefaultTierBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "cli";
        AppSettings.SetDefaultTier("claude", tier);
        AppSettings.SetDefaultTier("codex", tier);
    }

    /// <summary>
    /// One row per CLI and tier: which model plays "flash" (fast) and "pro" (capable).
    /// The choices are the models the CLI itself reported this run; before its first
    /// chat has connected there is no list, so the row only offers the auto guess.
    /// </summary>
    private void BuildTierModelRows()
    {
        TierModelsPanel.Children.Clear();

        foreach (var (cli, title) in new[] { ("claude", "Claude"), ("codex", "Codex") })
        {
            var catalogue = ChatViewModel.KnownCatalogue(cli);

            foreach (var (tier, tierTitle) in new[] { ("flash", "Flash"), ("pro", "Pro") })
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                label.Children.Add(new TextBlock { Text = $"{title} {tierTitle} model" });
                label.Children.Add(new TextBlock
                {
                    Text = catalogue is null
                        ? $"Open a {title} chat once to load its model list"
                        : tier == "flash"
                            ? "Runs the Lite and Flash tiers"
                            : "Runs the Pro and Plus tiers",
                    FontSize = 11,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                });
                row.Children.Add(label);

                var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
                combo.Items.Add(new ComboBoxItem { Content = "Auto (pick from model names)", Tag = "" });
                var configured = AppSettings.TierModelFor(cli, tier) ?? "";
                var selected = 0;
                if (catalogue is { Count: > 0 })
                {
                    foreach (var model in catalogue)
                    {
                        combo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(model.DisplayName) ? model.Id : model.DisplayName, Tag = model.Id });
                        if (string.Equals(model.Id, configured, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = combo.Items.Count - 1;
                        }
                    }
                }
                else if (configured.Length > 0)
                {
                    combo.Items.Add(new ComboBoxItem { Content = configured, Tag = configured });
                    selected = 1;
                }

                combo.SelectedIndex = selected;
                var pickedTier = tier;
                var pickedCli = cli;
                combo.SelectionChanged += (sender, _) =>
                {
                    if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item)
                    {
                        AppSettings.SetTierModel(pickedCli, pickedTier, item.Tag as string);
                    }
                };
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);
                TierModelsPanel.Children.Add(row);
            }
        }
    }

    private void OnGitPollChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.GitPollSeconds = (int)Math.Round(sender.Value);
    }
}
