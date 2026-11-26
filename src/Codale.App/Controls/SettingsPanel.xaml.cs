using Codale.Agents;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

using Windows.System;

namespace Codale.App.Controls;

/// <summary>
/// The app Settings pane: the app-level preferences (<see cref="AppSettingsStore"/>) in
/// one place - editor and terminal typography, the default shell, chat defaults and
/// the git refresh pace. It slides in over the right edge of the main window. Every
/// change is written through at once and applied live to the open project windows
/// via <see cref="_settings.Changed"/>.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    /// <summary>Populating the controls must not write the values straight back out.</summary>
    private bool _loading = true;

    /// <summary>The providers being edited. Every change is saved to the settings database.</summary>
    private readonly List<ByokProvider> _providers = [];

    /// <summary>The user asked to dismiss the pane (close button).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The owning window runs interactive sign-in in its workspace terminal.</summary>
    public Func<string, string?, Task<bool>>? SignInAsync { get; set; }

    private KeyboardShortcuts _shortcuts = null!;
    private SyntaxSelection _syntax = null!;
    private AppSettingsStore _settings = null!;
    private IWindowOwner _owner = null!;
    private SmartExperienceHost _smart = null!;
    private IAgentSessionFactory _agentSessions = null!;

    public SettingsPanel()
    {
        InitializeComponent();
        // Subscribe only while visible in the visual tree; each window uses its composed store.
        Loaded += (_, _) => { _settings.Changed += OnAppSettingsChanged; _smart.Instance.Changed += OnSmartStatusChanged; RefreshSmartStatus(); RefreshSyntaxSection(); };
        Unloaded += (_, _) =>
        {
            _settings.Changed -= OnAppSettingsChanged; _smart.Instance.Changed -= OnSmartStatusChanged;
            if (_syntaxSubscribed) { _syntax.Store.Changed -= OnSyntaxStoreChanged; _syntaxSubscribed = false; }
        };

    }

    public void InitializeServices(SmartExperienceHost smart, IAgentSessionFactory agentSessions, AppSettingsStore settings, KeyboardShortcuts shortcuts, SyntaxSelection syntax, IWindowOwner owner)
    {
        _owner = owner;
        _settings = settings;
        _shortcuts = shortcuts;
        _syntax = syntax;
        _smart = smart;
        _agentSessions = agentSessions;
        Reload();
    }

    public void ShowAccountSetup() => TabNavAi.IsChecked = true;

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenInstallFolderClick(object sender, RoutedEventArgs e) => OpenFolder(AppContext.BaseDirectory);

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => OpenFolder(_settings.DataFolder);

    private void OnOpenTempFolderClick(object sender, RoutedEventArgs e) => OpenFolder(CodalePaths.TempDirectory);

    private async void OnClearTempFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear temporary files?",
            Content = "This deletes temporary files, including pasted attachments and browser downloads. Finish any active tasks that use them first.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Clear);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var root = CodalePaths.TempDirectory;
            if (!Directory.Exists(root)) return;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                await AppDialog.AlertAsync(XamlRoot, "Could not clear temporary files", "The temporary folder is a link. Open it to manage its contents instead.");
                return;
            }
            var failed = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                        Directory.Delete(entry, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            }
            if (failed) await AppDialog.AlertAsync(XamlRoot, "Some files could not be cleared", "Files in use or without permission were left in the temporary folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await AppDialog.AlertAsync(XamlRoot, "Could not clear temporary files", ex.Message);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            // Shell-open the folder itself; explorer.exe falls back to Documents when it can't parse its argument.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception)
        {
            // Nothing sensible to do if Explorer cannot be launched.
        }
    }

    private void OnCategoryTabChanged(object sender, RoutedEventArgs e)
    {
        if (SectionGeneral is null || SectionAi is null || SectionEditor is null || SectionTerminal is null || SectionGitShortcuts is null)
        {
            return;
        }

        SectionGeneral.Visibility = TabNavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionAi.Visibility = TabNavAi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionEditor.Visibility = TabNavEditor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionTerminal.Visibility = TabNavTerminal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionGitShortcuts.Visibility = TabNavGitShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (PageTitle is null || PageSubtitle is null)
        {
            return;
        }

        (PageTitle.Text, PageSubtitle.Text) =
            TabNavAi.IsChecked == true ? ("AI Providers", "Accounts, models and Smart features.")
            : TabNavEditor.IsChecked == true ? ("Editor", "How code is shown and edited.")
            : TabNavTerminal.IsChecked == true ? ("Terminal", "Shell, font and scrollback for terminal tabs.")
            : TabNavGitShortcuts.IsChecked == true ? ("Keyboard Shortcuts", "Click a shortcut, then press the new keys.")
            : ("General", "Appearance, folders and the chat.");
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        RefreshSmartStatus();
        ReloadProvidersIfEditedElsewhere();
        RefreshShortcutRows();
    }

    private void OnSmartStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshSmartStatus);
    private string? _smartRowsKey;
    private readonly Dictionary<string, AutoSuggestBox> _smartModelBoxes = [];
    private readonly HashSet<AutoSuggestBox> _loadingSmartModels = [];

    /// <summary>
    /// A model field is free text with catalogue suggestions. The text is the source of truth: catalogue
    /// loads only replace the suggestions, so a saved model the endpoint does not list is kept as typed.
    /// </summary>
    private static AutoSuggestBox CreateModelBox(object? header, string placeholder, string value, Action<string> save)
    {
        var box = new AutoSuggestBox
        {
            Header = header,
            PlaceholderText = placeholder,
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.TextChanged += (_, e) =>
        {
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) save(box.Text.Trim());
        };
        box.SuggestionChosen += (_, e) =>
        {
            if (e.SelectedItem is string id) box.Text = id;
            save(box.Text.Trim());
        };
        box.QuerySubmitted += (_, e) =>
        {
            box.Text = (e.ChosenSuggestion as string ?? e.QueryText).Trim();
            save(box.Text);
        };
        return box;
    }

    private void RefreshSmartFeatureRows()
    {
        // Use concrete account profiles, as account setup does. Chat aliases can fold a
        // detected account into a default row that no longer carries its credential home.
        var profiles = _settings.ByokProviders;
        var rows = new List<ProviderChoice>();
        foreach (var cli in new[] { "claude", "codex" })
        {
            var label = cli == "codex" ? "Codex" : "Claude";
            var supported = profiles.Where(p => p.Supports(cli)).ToArray();
            if (!supported.Any(p => p.IsAccount))
                rows.Add(new("", label + ": Sign-in account", "Sign-in account", cli));
            foreach (var profile in supported)
                rows.Add(new(profile.Key, label + ": " + (profile.IsAccount ? profile.Email ?? profile.Name : profile.Name),
                    profile.IsAccount ? "Sign-in account" : cli == "codex" ? profile.BaseUrl : profile.AnthropicBaseUrl, cli));
        }
        var key = string.Join("|", SmartFeatureSettings.Roles.Select(r => _settings.SmartProviderFor(r.Id) + ":" + _settings.SmartApiProfileFor(r.Id)))
            + "#" + string.Join("|", rows.Select(r => r.CliId + r.Name + r.Label + r.Detail))
            + "#" + string.Join("|", profiles.Where(p => p.IsAccount).Select(p => p.Key + ":" + p.AccountId));
        if (_smartRowsKey != key)
        {
            _smartRowsKey = key;
            _smartModelBoxes.Clear();
            SmartFeatureRows.Children.Clear();
            var first = true;
            foreach (var role in SmartFeatureSettings.Roles)
            {
                SmartFeatureRows.Children.Add(BuildSmartRoleRow(role, rows, profiles, first));
                first = false;
            }
        }
        foreach (var (id, box) in _smartModelBoxes)
        {
            if (!SmartFeatureSettings.RequiresDedicatedModel(id))
            {
                var flash = _smart.CreateClient(id).DefaultModel;
                box.PlaceholderText = flash.Length > 0 ? $"Flash: {flash}" : "Flash model";
            }
            RestoreSmartModelSelection(id, box);
        }
    }

    /// <summary>One block per model role: name on the left, profile above model on the right.</summary>
    private Grid BuildSmartRoleRow((string Id, string Label, string Description, string Placeholder) role,
        IReadOnlyList<ProviderChoice> rows, IReadOnlyList<ByokProvider> profiles, bool first)
    {
        var provider = _settings.SmartProviderFor(role.Id);
        var row = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 4,
            Padding = new Thickness(16, 10, 16, 10),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        label.Children.Add(new TextBlock { Text = role.Label, Style = (Style)SettingsRootGrid.Resources["RowTitle"] });
        label.Children.Add(new TextBlock { Text = role.Description, Style = (Style)SettingsRootGrid.Resources["RowDesc"] });
        row.Children.Add(label);
        Grid.SetRowSpan(label, 2);

        var providers = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        providers.Items.Add(new ComboBoxItem { Content = "Off", Tag = ("off", "") });
        var savedProfile = _settings.SmartApiProfileFor(role.Id);
        foreach (var choice in rows)
        {
            var isAccount = choice.Detail == "Sign-in account";
            var target = isAccount ? (choice.CliId, choice.Name) : (choice.CliId == "codex" ? "openai" : "anthropic", choice.Name);
            if (!SmartFeatureSettings.SupportsProvider(role.Id, target.Item1)) continue;
            providers.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = target });
        }
        var selectedProfile = savedProfile;
        if (provider is "claude" or "codex")
            selectedProfile = SmartFeatureSettings.ResolveAccount(profiles, provider, savedProfile, _settings.AccountLabel(provider))?.Key ?? savedProfile;
        var current = providers.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
            i.Tag is (string p, string profile) && p == provider && (provider == "off" || profile == selectedProfile));
        if (current is null && provider != "off")
        {
            current = new ComboBoxItem
            {
                Content = char.ToUpperInvariant(provider[0]) + provider[1..] +
                (SmartFeatureSettings.SupportsProvider(role.Id, provider) ? " (unavailable)" : " (needs API endpoint)"),
                Tag = (provider, savedProfile),
                IsEnabled = false
            };
            providers.Items.Add(current); // keep a saved choice visible even if its profile is gone
        }
        providers.SelectedItem = current ?? providers.Items[0];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(providers, role.Label + " provider");
        providers.SelectionChanged += (_, _) =>
        {
            if (_loading || providers.SelectedItem is not ComboBoxItem { Tag: ValueTuple<string, string> selection }) return;
            var (selected, profile) = selection;
            _settings.SetSmartProvider(role.Id, selected);
            _settings.SetSmartApiProfile(role.Id, profile);
            if (selected != "off" && _settings.SmartRoleModel(role.Id).Equals("off", StringComparison.OrdinalIgnoreCase))
                _settings.SetSmartRoleModel(role.Id, "default");
        };
        Grid.SetRow(providers, 0);
        Grid.SetColumn(providers, 1);
        Grid.SetColumnSpan(providers, 2);
        row.Children.Add(providers);

        var active = provider != "off" && SmartFeatureSettings.SupportsProvider(role.Id, provider);
        var model = CreateModelBox(null, role.Placeholder, _settings.SmartRoleModel(role.Id),
            value => { if (!_loading) _settings.SetSmartRoleModel(role.Id, value); });
        model.VerticalAlignment = VerticalAlignment.Center;
        model.Tag = role.Id;
        model.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(model, role.Label + " model");
        // Only problems are spelled out; a working row stays one line.
        var modelStatus = new TextBlock { Visibility = Visibility.Collapsed, Style = (Style)SettingsRootGrid.Resources["RowDesc"] };
        Grid.SetRow(modelStatus, 2); Grid.SetColumn(modelStatus, 1); Grid.SetColumnSpan(modelStatus, 2);
        if (active)
        {
            model.Loaded += async (_, _) => await LoadSmartModelsAsync(model, modelStatus, role.Id, provider);
            model.GotFocus += async (_, _) => await LoadSmartModelsAsync(model, modelStatus, role.Id, provider);
            _smartModelBoxes[role.Id] = model;
        }
        Grid.SetRow(model, 1); Grid.SetColumn(model, 1); Grid.SetColumnSpan(model, 2);
        row.Children.Add(model);
        row.Children.Add(modelStatus);
        return row;
    }

    /// <summary>Restores the persisted model after the editable ComboBox template and items have settled.</summary>
    private void RestoreSmartModelSelection(string id, AutoSuggestBox box)
    {
        if (box.FocusState != FocusState.Unfocused || _smartModelBoxes.GetValueOrDefault(id) != box) return;

        var model = _settings.SmartRoleModel(id);
        if (box.Text == model) return;
        var loading = _loading;
        _loading = true;
        try { box.Text = model; }
        finally { _loading = loading; }
    }

    private async Task LoadSmartModelsAsync(AutoSuggestBox box, TextBlock status, string role, string provider)
    {
        if (provider == "off" || !SmartFeatureSettings.SupportsProvider(role, provider)) return;
        if (!_loadingSmartModels.Add(box)) return;
        var profileKey = _settings.SmartApiProfileFor(role);
        try
        {
            var client = _smart.CreateClient(role);
            var accountId = client.AccountId;
            var ids = provider is "claude" or "codex"
                ? [.. (ChatViewModel.KnownCatalogue(provider, accountId) ?? await ChatViewModel.FetchCatalogueAsync(_agentSessions, provider, accountId)).Select(m => m.Id)]
                : await client.ModelsAsync(CancellationToken.None);
            if (provider != _settings.SmartProviderFor(role) || profileKey != _settings.SmartApiProfileFor(role) || !_smartModelBoxes.ContainsValue(box)) return;
            // Suggestions only: the typed or saved model stays in the box even when the catalogue omits it,
            // for example embedding-only models.
            box.ItemsSource = ids.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            status.Text = ids.Length == 0 ? "No models reported. Enter a model ID." : "";
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(box, ids.Length == 0 ? status.Text : null);
        }
        catch (Exception ex)
        {
            status.Text = $"Could not load models: {ex.Message}";
            status.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(box, status.Text);
        }
        finally
        {
            _loadingSmartModels.Remove(box);
            if (box.Tag is string id) RestoreSmartModelSelection(id, box);
        }
    }
    private void RefreshSmartStatus()
    {
        var status = _smart.Instance.Status;
        var loading = _loading; _loading = true;
        RefreshSmartFeatureRows();
        _loading = loading;
        SmartExperienceStatus.Text = status.Message;
        SmartExperienceStatusPanel.Visibility = status.State == Codale.Smart.SmartState.Error ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSmartExperienceRetry(object sender, RoutedEventArgs e) => _smart.Retry();

    /// <summary>Re-reads every setting into the controls; another window may have changed them since.</summary>
    public void Reload()
    {
        _loading = true;

        EditorFontSizeBox.Value = _settings.EditorFontSize;
        EditorTabWidthBox.SelectedIndex = _settings.EditorTabWidth switch
        {
            2 => 0,
            8 => 2,
            _ => 1,
        };
        EditorLineNumbersToggle.IsOn = _settings.EditorLineNumbers;
        EditorHighlightingToggle.IsOn = _settings.EditorHighlighting;
        RefreshSyntaxSection();
        WordWrapToggle.IsOn = _settings.EditorWordWrap;
        SuggestNextPromptToggle.IsOn = _settings.SuggestNextPrompt;
        RefreshSmartStatus();

        ThemeBox.SelectedIndex = _settings.Theme switch
        {
            "Light" => 1,
            "System" => 2,
            _ => 0,
        };

        TerminalFontFamilyBox.SelectedIndex = IndexOfFontFamily(_settings.TerminalFontFamily);
        TerminalShellBox.SelectedIndex = _settings.TerminalShell switch
        {
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 0,
        };
        TerminalFontSizeBox.Value = _settings.TerminalFontSize;
        TerminalScrollbackBox.SelectedIndex = _settings.TerminalScrollback switch
        {
            <= 1_000 => 0,
            <= 10_000 => 1,
            <= 50_000 => 2,
            _ => 3,
        };
        TerminalBlockLinesBox.Value = _settings.TerminalBlockLines;
        TerminalBlockHistoryBox.Value = _settings.TerminalBlockHistory;
        TerminalLoadProfileToggle.IsOn = _settings.TerminalLoadProfile;

        ChatFontSizeBox.Value = _settings.ChatFontSize;
        PopulateChatFontFamilies();

        GitPollBox.Value = _settings.GitPollSeconds;

        if (_shortcutRows.Count == 0)
        {
            BuildShortcutRows();
        }

        EndRecording();
        RefreshShortcutRows();

        _providers.Clear();
        _providers.AddRange(_settings.ByokProviders);
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
                _shortcuts.Set(action.Id, action.Default);
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

            var chord = _shortcuts.Get(action.Id);
            row.Chord.Content = _recordingId == action.Id ? "Press keys…" : chord.Length == 0 ? "Not set" : chord;
            row.Reset.IsEnabled = chord != action.Default;
        }
    }

    private void BeginRecording(string id)
    {
        EndRecording();
        _recordingId = id;
        _shortcuts.IsRecording = true;
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
        _shortcuts.IsRecording = false;
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
                _shortcuts.Set(id, KeyboardShortcuts.DoubleEscape);
            }

            EndRecording();
            return;
        }

        if (e.Key is VirtualKey.Back or VirtualKey.Delete && !KeyChord.AnyModifierDown())
        {
            _shortcuts.Set(id, "");
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

        if (_shortcuts.Owner(chord, id) is { } owner)
        {
            ShortcutHint.Text = $"{chord} is already used by \"{owner.Label}\". Change that one first.";
            return;
        }

        _shortcuts.Set(id, chord.ToString());
        EndRecording();
    }

    private void OnResetShortcutsClick(object sender, RoutedEventArgs e)
    {
        EndRecording();
        foreach (var action in KeyboardShortcuts.Actions)
        {
            _shortcuts.Set(action.Id, action.Default);
        }

        RefreshShortcutRows();
    }

    private bool _refreshingAccounts;
    private readonly HashSet<string> _removingAccounts = [];
    private readonly HashSet<string> _signingInAccounts = [];
    private string? _expandedAccountKey;
    private static string AccountKey(string cli, string accountId) => cli + ":" + accountId;

    /// <summary>Detect accounts from stored CLI credentials whenever Settings opens.</summary>
    public async Task RefreshAccountsAsync()
    {
        if (_refreshingAccounts) return;
        _refreshingAccounts = true;
        try
        {
            foreach (var cli in new[] { "claude", "codex" })
            {
                var status = cli == "codex" ? CodexAccountStatus : ClaudeAccountStatus;
                try
                {
                    var accounts = await CodaleAccountCli.DiscoverAccountsAsync(cli);
                    foreach (var account in accounts)
                    {
                        var key = AccountKey(cli, account.AccountId);
                        if (_removingAccounts.Contains(key)) continue;
                        var profile = _providers.FirstOrDefault(p => p.IsAccount && p.CliId == cli && p.AccountId == account.AccountId);
                        if (profile is null)
                        {
                            profile = new ByokProvider
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                CliId = cli,
                                IsAccount = true,
                                AccountId = account.AccountId,
                                Name = account.Email ?? (account.AccountId == CodaleAccount.UserAccountId ? "Detected CLI account" : "Detected account")
                            };
                            _providers.Add(profile);
                        }
                        if (account.Email is { Length: > 0 } email
                            && (profile.Name is "Detected CLI account" or "Detected account" or "Connected account"
                                || profile.Name == profile.Email))
                            profile.Name = email;
                        profile.Email = account.Email;
                    }
                    WriteSettings(() => _settings.SetAccountLabel(cli, accounts.FirstOrDefault(a => a.AccountId == "")?.Email));
                    status.Text = accounts.Count == 0 ? "No accounts detected" : $"{accounts.Count} account{(accounts.Count == 1 ? "" : "s")} detected";
                }
                catch (Exception ex) { status.Text = $"Could not detect accounts: {ex.Message}"; }
            }
            SaveProviders();
            BuildProviderEditors();
        }
        finally { _refreshingAccounts = false; }
    }

    private async void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var cli = (string)button.Tag;
        var status = cli == "codex" ? CodexAccountStatus : ClaudeAccountStatus;
        var id = Guid.NewGuid().ToString("N");
        button.IsEnabled = false;
        try
        {
            if (SignInAsync is null) throw new InvalidOperationException("Account setup is unavailable in this window.");
            status.Text = "Connecting account…";
            var (signedIn, email) = await SignInAsync(cli, id)
                ? await CodaleAccountCli.GetAccountAsync(cli, accountId: id)
                : (false, null);
            if (!signedIn) { status.Text = "Account setup did not complete. No account was added."; return; }
            if (!_providers.Any(p => p.IsAccount && p.CliId == cli && p.AccountId == id))
                _providers.Add(new ByokProvider
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = email ?? "Connected account",
                    CliId = cli,
                    IsAccount = true,
                    AccountId = id,
                    Email = email
                });
            _expandedAccountKey = _providers.First(p => p.IsAccount && p.CliId == cli && p.AccountId == id).Key;
            SaveProviders();
            BuildProviderEditors();
            await RefreshAccountsAsync();
            await ReloadCatalogueAfterAccountChangeAsync(cli);
        }
        catch (Exception ex) { status.Text = $"Could not add account: {ex.Message}"; }
        finally { button.IsEnabled = true; }
    }

    private async Task RemoveAccountAsync(ByokProvider provider, string cli, Button button, TextBlock status)
    {
        var key = AccountKey(cli, provider.AccountId);
        if (!_removingAccounts.Add(key)) return;
        button.IsEnabled = false;
        try
        {
            status.Text = "Removing account…";
            if (!await AccountProfileRemoval.RemoveAsync(provider,
                p => CodaleAccountCli.SignOutAsync(cli, accountId: p.AccountId),
                p => _providers.RemoveAll(candidate => candidate.IsAccount && candidate.CliId == cli && candidate.AccountId == p.AccountId)))
            { status.Text = "Could not sign out. The account was kept; try removing it again."; return; }
            if (provider.AccountId.Length == 0) WriteSettings(() => _settings.SetAccountLabel(cli, null));
            ChatViewModel.ClearAccountRejection(cli, provider.AccountId);
            SaveProviders();
            BuildProviderEditors();
            await RefreshAccountsAsync();
            await ReloadCatalogueAfterAccountChangeAsync(cli);
        }
        catch (Exception ex) { status.Text = $"Could not remove account: {ex.Message}"; }
        finally { _removingAccounts.Remove(key); button.IsEnabled = true; }
    }

    /// <summary>A different account (or none) may have a different plan, so the old model list is dropped and read again.</summary>
    private Task ReloadCatalogueAfterAccountChangeAsync(string cli)
    {
        ChatViewModel.ForgetCatalogue(cli);
        return Task.CompletedTask;
    }

    private void BuildProviderEditors()
    {
        ProvidersPanel.Children.Clear();
        CodexProfilesPanel.Children.Clear();
        foreach (var cli in new[] { "claude", "codex" })
            foreach (var provider in _providers.Where(p => p.Supports(cli)))
            {
                (cli == "codex" ? CodexProfilesPanel : ProvidersPanel).Children.Add(BuildProviderEditor(provider, expanded: provider.Key == _expandedAccountKey, cli));
            }

    }

    private Expander BuildProviderEditor(ByokProvider provider, bool expanded, string cli)
    {
        var header = new TextBlock { Text = provider.Email ?? provider.Name };
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

        Field("Name", "Shown in the status bar", provider.Name, v => provider.Name = v, () => header.Text = provider.Name);

        if (!provider.IsAccount && cli == "codex") Field("OpenAI-compatible base URL", "https://api.example.com/v1 (used by Codex CLI)", provider.BaseUrl, v => provider.BaseUrl = v);
        else if (!provider.IsAccount) Field("Anthropic-compatible base URL", "https://api.example.com (used by Claude CLI)", provider.AnthropicBaseUrl, v => provider.AnthropicBaseUrl = v);

        TextBlock? accountStatus = null;
        if (provider.IsAccount)
        {
            accountStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(accountStatus);
        }
        if (!provider.IsAccount)
        {
            var key = new PasswordBox { Header = "API key", Password = provider.ApiKey };
            key.PasswordChanged += (_, _) =>
            {
                provider.ApiKey = key.Password;
                SaveProviders();
            };
            panel.Children.Add(key);
        }

        // Accounts and endpoints share model controls; their catalogues use their own credentials.
        var modelBoxes = new List<AutoSuggestBox>();

        AutoSuggestBox ModelField(string label, string placeholder, string value, Action<string> apply)
        {
            var box = CreateModelBox(label, placeholder, value, v => { apply(v); SaveProviders(); });
            box.GotFocus += async (_, _) => await LoadModelsAsync(provider, modelBoxes);
            panel.Children.Add(box);
            modelBoxes.Add(box);
            return box;
        }

        ModelField("Flash model", provider.IsAccount ? "CLI default" : "Select or enter a model ID", provider.FlashModel, v => provider.FlashModel = v);
        ModelField("Pro model", "Flash model when blank", provider.ProModel, v => provider.ProModel = v);

        if (accountStatus is not null)
        {
            var accountId = provider.AccountId;
            var key = AccountKey(cli, accountId);
            var removeAccount = new Button { Content = "Remove account" };
            var signIn = new Button { Content = "Sign in", Visibility = Visibility.Collapsed };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            actions.Children.Add(removeAccount);
            actions.Children.Add(signIn);
            panel.Children.Add(actions);

            void ShowStatus(bool signedIn, string? email, string? message = null)
            {
                accountStatus.Text = message ?? (signedIn
                    ? (email is null ? "Signed in" : $"Signed in as {email}")
                    : "Sign-in required. Your session may have expired.");
                signIn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            }

            bool checking = false;
            async Task CheckAccountAsync()
            {
                if (checking || _removingAccounts.Contains(key)) return;
                if (_signingInAccounts.Contains(key))
                {
                    accountStatus.Text = "Signing in…";
                    removeAccount.IsEnabled = false;
                    return;
                }
                checking = true;
                accountStatus.Text = "Checking sign-in…";
                signIn.Visibility = Visibility.Collapsed;
                removeAccount.IsEnabled = false;
                try
                {
                    var (signedIn, email) = await CodaleAccountCli.GetAccountAsync(cli, accountId: accountId);
                    ShowStatus(signedIn && !ChatViewModel.IsAccountRejected(cli, accountId), email ?? provider.Email);
                }
                catch (Exception ex) { ShowStatus(false, null, $"Could not check sign-in: {ex.Message}"); }
                finally { checking = false; removeAccount.IsEnabled = true; }
            }

            expander.Expanding += async (_, _) =>
            {
                _expandedAccountKey = provider.Key;
                await CheckAccountAsync();
            };
            expander.Collapsed += (_, _) =>
            {
                if (_expandedAccountKey == provider.Key) _expandedAccountKey = null;
            };
            expander.Loaded += async (_, _) =>
            {
                if (expander.IsExpanded) await CheckAccountAsync();
            };
            removeAccount.Click += async (_, _) =>
            {
                if (_signingInAccounts.Contains(key)) return;
                signIn.IsEnabled = false;
                try { await RemoveAccountAsync(provider, cli, removeAccount, accountStatus); }
                finally { signIn.IsEnabled = true; }
            };
            signIn.Click += async (_, _) =>
            {
                if (_removingAccounts.Contains(key) || !_signingInAccounts.Add(key)) return;
                signIn.IsEnabled = removeAccount.IsEnabled = false;
                try
                {
                    if (SignInAsync is null) throw new InvalidOperationException("Account setup is unavailable in this window.");
                    accountStatus.Text = "Signing in…";
                    var (signedIn, email) = await SignInAsync(cli, accountId)
                        ? await CodaleAccountCli.GetAccountAsync(cli, accountId: accountId)
                        : (false, null);
                    if (!signedIn)
                    {
                        ShowStatus(false, null, "Sign-in did not complete. Try again.");
                        return;
                    }

                    ChatViewModel.ClearAccountRejection(cli, accountId);
                    if (accountId.Length == 0 && email is not null)
                        WriteSettings(() => _settings.SetAccountLabel(cli, email));
                    if (email is not null && _providers.FirstOrDefault(p => p.IsAccount && p.CliId == cli && p.AccountId == accountId) is { } saved)
                    {
                        saved.Email = email;
                        header.Text = email;
                        SaveProviders();
                    }
                    await ReloadCatalogueAfterAccountChangeAsync(cli);
                    ShowStatus(true, email ?? provider.Email);
                }
                catch (Exception ex) { ShowStatus(false, null, $"Could not sign in: {ex.Message}"); }
                finally
                {
                    _signingInAccounts.Remove(key);
                    signIn.IsEnabled = removeAccount.IsEnabled = true;
                    if (expander.Parent is null) BuildProviderEditors();
                }
            };
            expander.Content = panel;
            return expander;
        }
        var remove = new Button { Content = "Remove provider", HorizontalAlignment = HorizontalAlignment.Right };
        remove.Click += (_, _) =>
        {
            _providers.Remove(provider);
            SaveProviders();
            BuildProviderEditors();
        };
        panel.Children.Add(remove);

        expander.Content = panel;
        return expander;
    }

    private static readonly System.Net.Http.HttpClient ModelHttp = new() { Timeout = TimeSpan.FromSeconds(10) };


    /// <summary>Shows why the list could not load as a tooltip on the model boxes; null clears it.</summary>
    private static void SetModelStatus(List<AutoSuggestBox> boxes, string? message)
    {
        foreach (var box in boxes)
        {
            ToolTipService.SetToolTip(box, message);
        }
    }

    /// <summary>Fills the model dropdowns from the endpoint's OpenAI-style GET /models; failures leave the current list.</summary>
    private async Task LoadModelsAsync(ByokProvider provider, List<AutoSuggestBox> boxes)
    {
        if (provider.IsAccount)
        {
            var accountModels = ChatViewModel.KnownCatalogue(provider.CliId, provider.AccountId)
                ?? await ChatViewModel.FetchCatalogueAsync(_agentSessions, provider.CliId, provider.AccountId);
            if (accountModels.Count == 0)
            {
                SetModelStatus(boxes, "No models reported for this account. Enter a model ID manually.");
                return;
            }
            SetModelStatus(boxes, null);
            var accountIds = accountModels.Select(model => model.Id).ToList();
            foreach (var box in boxes) box.ItemsSource = accountIds;
            return;
        }
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

        List<(string Id, int? ContextWindow)> models;
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
            if (anthropicOnly && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                foreach (var box in boxes) box.ItemsSource = null;
                SetModelStatus(boxes, "This endpoint did not provide a model list. Enter a model ID supported by your provider.");
                return;
            }
            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            models = OpenAiModelParser.ReadModels(doc.RootElement);
            if (models.Count == 0)
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
        provider.ModelContextWindows ??= [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var (id, contextWindow) in models)
        {
            if (contextWindow is int window && window > 0)
            {
                provider.ModelContextWindows[id] = window;
            }
        }
        var ids = models.Select(model => model.Id).ToList();
        SaveProviders();

        foreach (var box in boxes) box.ItemsSource = ids;
    }

    private void SaveProviders()
    {
        if (!_loading)
        {
            WriteSettings(() => _settings.ByokProviders = _providers);
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

    /// <summary>Refreshes the provider editor when the saved list changes.</summary>
    private void ReloadProvidersIfEditedElsewhere()
    {
        if (_loading || _selfWrite)
        {
            return;
        }

        var fresh = _settings.ByokProviders;
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

        var provider = new ByokProvider { Id = Guid.NewGuid().ToString("N"), Name = name, CliId = (string)((FrameworkElement)sender).Tag };
        _providers.Add(provider);
        SaveProviders();
        BuildProviderEditors();
    }

    /// <summary>The chat reading fonts worth offering; a stored custom family stays selectable.</summary>
    private void PopulateChatFontFamilies()
    {
        ChatFontFamilyBox.Items.Clear();
        foreach (var family in new[] { "Segoe UI", "Segoe UI Variable", "Calibri", "Candara", "Corbel", "Georgia", "Verdana", "Cascadia Mono" })
        {
            ChatFontFamilyBox.Items.Add(new ComboBoxItem { Content = family });
        }

        var stored = _settings.ChatFontFamily;
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

        _settings.EditorFontSize = sender.Value;
    }

    private void OnEditorTabWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EditorTabWidth = EditorTabWidthBox.SelectedIndex switch
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
            _settings.EditorLineNumbers = EditorLineNumbersToggle.IsOn;
        }
    }

    // ------------------------------------------------------------------ syntax

    private bool _syntaxSubscribed;
    private void OnSyntaxStoreChanged() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) RefreshSyntaxSection(); });

    /// <summary>Rebuilds the installed-languages list and the override count from the store and settings.</summary>
    private void RefreshSyntaxSection()
    {
        _syntax.Start();
        RefreshSyntaxThemes();
        if (!_syntaxSubscribed)
        {
            // A grammar installed from the status bar (or by a download finishing) shows up here too.
            _syntaxSubscribed = true;
            _syntax.Store.Changed += OnSyntaxStoreChanged;
        }

        var all = _syntax.Catalog.All;
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
            remove.Click += (_, _) => _syntax.Store.Remove(language.Id);
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

        var overrides = _settings.SyntaxOverrides.Count;
        SyntaxOverridesHint.Text = overrides == 0
            ? "Files you set by hand from the status bar appear here."
            : $"{overrides} file{(overrides == 1 ? "" : "s")} set by hand from the status bar.";
        SyntaxClearOverridesButton.IsEnabled = overrides > 0;
    }

    private async Task ExportGrammarAsync(LanguageInfo language)
    {
        if (_syntax.Store.Find(language.Id) is not { } entry || _syntax.Store.ReadGrammar(entry) is not { } json)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{language.Id}.tmLanguage" };
        picker.FileTypeChoices.Add("TextMate grammar", [".json"]);
        PickerOwner.Attach(picker, _owner.WindowHandle);

        if (await picker.PickSaveFileAsync() is { } file)
        {
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }
    }

    private void OnSyntaxDownloadClick(object sender, RoutedEventArgs e) => _ = SyntaxDialogs.ShowCatalogAsync(XamlRoot);

    private bool _loadingSyntaxThemes;
    private void RefreshSyntaxThemes()
    {
        _loadingSyntaxThemes = true;
        try
        {
            foreach (var (box, type, selected) in new[] {
                (DarkSyntaxThemeBox, "dark", _settings.EditorDarkSyntaxTheme),
                (LightSyntaxThemeBox, "light", _settings.EditorLightSyntaxTheme) })
            {
                var available = SyntaxThemeCatalog.Entries.Where(t => t.Type == type)
                    .Where(t => File.Exists(SyntaxThemeCatalog.PathFor(_syntax.Store, t.Id))).OrderBy(t => t.Name).ToArray();
                // Theme changes also invalidate syntax. Keep live combo items stable while a popup closes.
                if (!box.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { "" }.Concat(available.Select(t => t.Id))))
                {
                    box.Items.Clear();
                    box.Items.Add(new ComboBoxItem { Content = "Codale default", Tag = "" });
                    foreach (var entry in available)
                        box.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });
                }
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? box.Items[0];
            }
        }
        finally { _loadingSyntaxThemes = false; }
    }

    private void OnSyntaxThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _loadingSyntaxThemes || sender is not ComboBox { SelectedItem: ComboBoxItem item } box) return;
        if (box == DarkSyntaxThemeBox) _settings.EditorDarkSyntaxTheme = (string)item.Tag;
        else _settings.EditorLightSyntaxTheme = (string)item.Tag;
    }

    private async void OnSyntaxThemesDownloadClick(object sender, RoutedEventArgs e)
    {
        await SyntaxDialogs.ShowThemesAsync(XamlRoot);
        RefreshSyntaxThemes();
    }

    private void OnSyntaxImportClick(object sender, RoutedEventArgs e)
    {
        if (_owner.WindowHandle is { } hwnd)
        {
            _ = SyntaxDialogs.ImportAsync(XamlRoot, hwnd);
        }
    }

    private void OnSyntaxClearOverridesClick(object sender, RoutedEventArgs e)
    {
        _settings.SyntaxOverrides = [];

        // Open editors re-detect their language; the store event is the one nudge they already listen for.
        _syntax.Store.NotifyChanged();
    }

    private void OnSyntaxOpenFolderClick(object sender, RoutedEventArgs e)
    {
        _syntax.Start();
        Directory.CreateDirectory(_syntax.Store.Root);
        _ = Windows.System.Launcher.LaunchFolderPathAsync(_syntax.Store.Root);
    }

    private void OnEditorHighlightingToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.EditorHighlighting = EditorHighlightingToggle.IsOn;
        }
    }

    private void OnSuggestNextPromptToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.SuggestNextPrompt = SuggestNextPromptToggle.IsOn;
        }
    }

    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.EditorWordWrap = WordWrapToggle.IsOn;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            _settings.Theme = ThemeBox.SelectedIndex switch
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
            _settings.TerminalFontFamily =
                (TerminalFontFamilyBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Cascadia Mono";
        }
    }

    private void OnChatFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        _settings.ChatFontSize = sender.Value;
    }

    private void OnChatFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            _settings.ChatFontFamily =
                (ChatFontFamilyBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Segoe UI";
        }
    }

    private void OnTerminalShellChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.TerminalShell = TerminalShellBox.SelectedIndex switch
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

        _settings.TerminalFontSize = sender.Value;
    }

    private void OnTerminalBlockLinesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        _settings.TerminalBlockLines = (int)Math.Round(sender.Value);
    }

    private void OnTerminalBlockHistoryChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        _settings.TerminalBlockHistory = (int)Math.Round(sender.Value);
    }

    private void OnTerminalLoadProfileToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.TerminalLoadProfile = TerminalLoadProfileToggle.IsOn;
    }

    private void OnTerminalScrollbackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.TerminalScrollback = TerminalScrollbackBox.SelectedIndex switch
        {
            0 => 1_000,
            2 => 50_000,
            3 => 100_000,
            _ => 10_000,
        };
    }

    private void OnGitPollChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        _settings.GitPollSeconds = (int)Math.Round(sender.Value);
    }
}
