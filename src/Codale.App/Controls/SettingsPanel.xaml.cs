using Codale.Agents.Claude;
using Codale.Agents;
using Codale.App.Services;
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
        BuildDefaultModelItems();
        DefaultEffortBox.SelectedIndex = AppSettings.DefaultEffort switch
        {
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            "xhigh" => 4,
            "max" => 5,
            _ => 0,
        };

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

    /// <summary>Rebuilds the background-task provider list: "First available", then each provider by name.</summary>
    private void RefreshHelperProviderBox()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var wanted = AppSettings.HelperProvider;
            HelperProviderBox.Items.Clear();
            var named = _providers.Where(p => p.Name.Trim().Length > 0).ToList();

            // No custom provider to call: background tasks run on the Claude CLI, so say so.
            HelperProviderBox.IsEnabled = named.Count > 0;
            if (named.Count == 0)
            {
                HelperProviderBox.Items.Add("Claude CLI");
                HelperProviderBox.SelectedIndex = 0;
                return;
            }

            HelperProviderBox.Items.Add("First available");
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
        }
    }

    private void OnHelperProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && HelperProviderBox.SelectedIndex >= 0)
        {
            var name = HelperProviderBox.SelectedIndex == 0 ? "" : HelperProviderBox.SelectedItem as string ?? "";
            WriteSettings(() => AppSettings.HelperProvider = name);
            RefreshTokenSaverHint();
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

            // Renaming the provider in use keeps it selected.
            if (string.Equals(AppSettings.ByokSelected, previousName, StringComparison.OrdinalIgnoreCase) &&
                provider.Name.Length > 0)
            {
                WriteSettings(() => AppSettings.ByokSelected = provider.Name);
            }

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
        Field("Anthropic Messages base URL", "https://gateway.example.com", provider.BaseUrl, v => provider.BaseUrl = v);

        var key = new PasswordBox { Header = "API key", Password = provider.ApiKey };
        key.PasswordChanged += (_, _) =>
        {
            provider.ApiKey = key.Password;
            SaveProviders();
        };
        panel.Children.Add(key);

        Field("Default model", "e.g. claude-sonnet-5-5", provider.Model, v => provider.Model = v);
        Field("Smart model", "e.g. claude-opus-5-5 (optional)", provider.SmartModel, v => provider.SmartModel = v);

        var remove = new Button { Content = "Remove provider", HorizontalAlignment = HorizontalAlignment.Right };
        remove.Click += (_, _) =>
        {
            _providers.Remove(provider);
            if (string.Equals(AppSettings.ByokSelected, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                WriteSettings(() => AppSettings.ByokSelected = "");
            }

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

    /// <summary>
    /// Fills the model picker: CLI default, then Claude's documented models. A stored
    /// id the catalogue no longer lists stays selectable so the user can see and clear it.
    /// </summary>
    private void BuildDefaultModelItems()
    {
        DefaultModelBox.Items.Clear();
        DefaultModelBox.Items.Add(new ComboBoxItem { Content = "CLI default", Tag = "" });
        var index = AppSettings.DefaultModel.Length == 0 ? 0 : -1;
        foreach (var model in ClaudeAgentSession.DocumentedModels)
        {
            DefaultModelBox.Items.Add(new ComboBoxItem { Content = $"{model.DisplayName}  ({model.Id})", Tag = model.Id });
            if (string.Equals(AppSettings.DefaultModel, model.Id, StringComparison.OrdinalIgnoreCase))
            {
                index = DefaultModelBox.Items.Count - 1;
            }
        }

        if (index < 0)
        {
            DefaultModelBox.Items.Add(new ComboBoxItem { Content = AppSettings.DefaultModel, Tag = AppSettings.DefaultModel });
            index = DefaultModelBox.Items.Count - 1;
        }

        DefaultModelBox.SelectedIndex = index;
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
            () => AppSettings.HelperApiEndpoint is { } api ? new AnthropicEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null);
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

    private void OnDefaultModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.DefaultModel = (DefaultModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }
    }

    private void OnDefaultEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            AppSettings.DefaultEffort = DefaultEffortBox.SelectedIndex switch
            {
                1 => "low",
                2 => "medium",
                3 => "high",
                4 => "xhigh",
                5 => "max",
                _ => "",
            };
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

    private void OnGitPollChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.GitPollSeconds = (int)Math.Round(sender.Value);
    }
}
