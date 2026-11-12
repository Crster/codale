using Codale.Agents;
using Codale.Core.Agents;
using Codale.Agents.Claude;
using Codale.Core.Helper;
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

    /// <summary>The owning window runs interactive sign-in in its workspace terminal.</summary>
    public Func<string, string?, Task>? SignInAsync { get; set; }

    public SettingsPanel()
    {
        InitializeComponent();
        // Only listen while in the visual tree: the settings event is static and windows come and go.
        Loaded += (_, _) => { AppSettings.Changed += OnAppSettingsChanged; SmartExperienceHost.Instance.Changed += OnSmartStatusChanged; RefreshSmartStatus(); };
        Unloaded += (_, _) => { AppSettings.Changed -= OnAppSettingsChanged; SmartExperienceHost.Instance.Changed -= OnSmartStatusChanged; };

        Reload();
    }

    public void ShowAccountSetup() => TabNavAi.IsChecked = true;

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenInstallFolderClick(object sender, RoutedEventArgs e) => OpenFolder(AppContext.BaseDirectory);

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => OpenFolder(McpServerSettings.CodaleDataRoot());

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            _ = Launcher.LaunchFolderPathAsync(path);
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
    private readonly Dictionary<string, ComboBox> _smartModelBoxes = [];
    private readonly HashSet<ComboBox> _loadingSmartModels = [];

    private void RefreshSmartFeatureRows()
    {
        // Use concrete account profiles, as account setup does. Chat aliases can fold a
        // detected account into a default row that no longer carries its credential home.
        var profiles = AppSettings.ByokProviders;
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
        var key = string.Join("|", SmartFeatureSettings.Groups.Select(g => AppSettings.SmartProviderFor(g.Id) + ":" + AppSettings.SmartApiProfileFor(g.Id)))
            + "#" + string.Join("|", rows.Select(r => r.CliId + r.Name + r.Label + r.Detail))
            + "#" + string.Join("|", profiles.Where(p => p.IsAccount).Select(p => p.Key + ":" + p.AccountId));
        if (_smartRowsKey != key)
        {
            _smartRowsKey = key;
            _smartModelBoxes.Clear();
            SmartFeatureRows.Children.Clear();
            foreach (var group in SmartFeatureSettings.Groups)
            {
                var provider = AppSettings.SmartProviderFor(group.Id);
                var section = new StackPanel { Spacing = 8 };
                var heading = new Grid { ColumnSpacing = 16 };
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = group.Label, Style = (Style)SettingsRootGrid.Resources["RowTitle"] });
                label.Children.Add(new TextBlock { Text = group.Description, TextWrapping = TextWrapping.Wrap, Style = (Style)SettingsRootGrid.Resources["RowDesc"] });
                heading.Children.Add(label);
                var providers = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                providers.Items.Add(new ComboBoxItem { Content = "Off", Tag = ("off", "") });
                var savedProfile = AppSettings.SmartApiProfileFor(group.Id);
                foreach (var row in rows)
                {
                    var isAccount = row.Detail == "Sign-in account";
                    var target = isAccount ? (row.CliId, row.Name) : (row.CliId == "codex" ? "openai" : "anthropic", row.Name);
                    if (!SmartFeatureSettings.SupportsProvider(group.Id, target.Item1)) continue;
                    providers.Items.Add(new ComboBoxItem { Content = row.Label, Tag = target });
                }
                var selectedProfile = savedProfile;
                if (provider is "claude" or "codex")
                    selectedProfile = SmartFeatureSettings.ResolveAccount(profiles, provider, savedProfile, AppSettings.AccountLabel(provider))?.Key ?? savedProfile;
                var current = providers.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
                    i.Tag is (string p, string profile) && p == provider && (provider == "off" || profile == selectedProfile));
                if (current is null && provider != "off")
                {
                    current = new ComboBoxItem { Content = char.ToUpperInvariant(provider[0]) + provider[1..] +
                        (SmartFeatureSettings.SupportsProvider(group.Id, provider) ? " (unavailable)" : " (requires API endpoint)"),
                        Tag = (provider, savedProfile), IsEnabled = false };
                    providers.Items.Add(current); // keep a saved choice visible even if its profile is gone
                }
                providers.SelectedItem = current ?? providers.Items[0];
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(providers, group.Label + " provider");
                providers.SelectionChanged += (_, _) =>
                {
                    if (_loading || providers.SelectedItem is not ComboBoxItem { Tag: ValueTuple<string, string> selection }) return;
                    var (selected, profile) = selection;
                    AppSettings.SetSmartProvider(group.Id, selected);
                    AppSettings.SetSmartApiProfile(group.Id, profile);
                    if (selected != "off" && AppSettings.SmartFeatureModel(group.Id).Equals("off", StringComparison.OrdinalIgnoreCase))
                        AppSettings.SetSmartFeatureModel(group.Id, "default");
                };
                Grid.SetColumn(providers, 1);
                heading.Children.Add(providers);
                section.Children.Add(heading);
                var model = new ComboBox { IsEditable = true, PlaceholderText = group.Id switch
                    { "completion" => "Select FIM model", "embedding" => "Select embedding model", _ => "Account Flash model" },
                    HorizontalAlignment = HorizontalAlignment.Right, Width = 220, Tag = group.Id,
                    IsEnabled = provider != "off" && SmartFeatureSettings.SupportsProvider(group.Id, provider) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(model, group.Label + " model");
                model.LostFocus += (_, _) => { if (!_loading && _smartModelBoxes.GetValueOrDefault(group.Id) == model) AppSettings.SetSmartFeatureModel(group.Id, model.Text); };
                model.TextSubmitted += (_, args) => { if (!_loading) AppSettings.SetSmartFeatureModel(group.Id, args.Text); };
                model.SelectionChanged += (_, _) => { if (!_loading && model.SelectedItem is string id) AppSettings.SetSmartFeatureModel(group.Id, id); };
                var modelStatus = new TextBlock { Width = 220, HorizontalAlignment = HorizontalAlignment.Right,
                    TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                    Style = (Style)SettingsRootGrid.Resources["RowDesc"] };
                model.Loaded += async (_, _) => await LoadSmartModelsAsync(model, modelStatus, group.Id, provider);
                model.DropDownOpened += async (_, _) => await LoadSmartModelsAsync(model, modelStatus, group.Id, provider);
                section.Children.Add(model);
                section.Children.Add(modelStatus);
                _smartModelBoxes[group.Id] = model;
                SmartFeatureRows.Children.Add(section);
            }
        }
        foreach (var (id, box) in _smartModelBoxes)
        {
            if (!SmartFeatureSettings.RequiresDedicatedModel(id))
            {
                var flash = SmartExperienceHost.CreateClient(id).DefaultModel;
                box.PlaceholderText = flash.Length > 0 ? $"Flash model: {flash}" : "Account Flash model";
            }
            if (box.FocusState == FocusState.Unfocused)
            {
                var model = AppSettings.SmartFeatureModel(id);
                box.Text = model;
            }
        }
    }

    private async Task LoadSmartModelsAsync(ComboBox box, TextBlock status, string group, string provider)
    {
        if (provider == "off") return;
        if (!SmartFeatureSettings.SupportsProvider(group, provider))
        {
            status.Text = "Choose a compatible API endpoint for this feature.";
            status.Visibility = Visibility.Visible;
            return;
        }
        if (!_loadingSmartModels.Add(box)) return;
        status.Text = "Loading models…";
        status.Visibility = Visibility.Visible;
        var profileKey = AppSettings.SmartApiProfileFor(group);
        try
        {
            var client = SmartExperienceHost.CreateClient(group);
            var accountId = client.AccountId;
            var ids = provider is "claude" or "codex"
                ? (ChatViewModel.KnownCatalogue(provider, accountId) ?? await ChatViewModel.FetchCatalogueAsync(provider, accountId)).Select(m => m.Id).ToArray()
                : await client.ModelsAsync(CancellationToken.None);
            if (provider != AppSettings.SmartProviderFor(group) || profileKey != AppSettings.SmartApiProfileFor(group) || !_smartModelBoxes.ContainsValue(box)) return;
            var current = box.Text;
            var loading = _loading; _loading = true;
            try
            {
                box.Items.Clear();
                foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    box.Items.Add(id);
                }
                box.Text = current;
            }
            finally { _loading = loading; }
            status.Text = ids.Length == 0 ? "No models reported. Check the provider or enter a model ID." : group switch
            {
                "completion" => "Choose a FIM model served by a completions endpoint with prefix/suffix support.",
                "embedding" => "Choose a model that returns embedding vectors. Chat models cannot be used here.",
                _ => "",
            };
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(box, ids.Length == 0 ? status.Text : null);
        }
        catch (Exception ex)
        {
            status.Text = $"Could not load models: {ex.Message}";
            status.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(box, status.Text);
        }
        finally { _loadingSmartModels.Remove(box); }
    }
    private void RefreshSmartStatus()
    {
        var status = SmartExperienceHost.Instance.Status;
        var loading = _loading; _loading = true;
        RefreshSmartFeatureRows();
        _loading = loading;
        SmartExperienceStatus.Text = status.Message;
        SmartExperienceStatusPanel.Visibility = status.State == Codale.Smart.SmartState.Error ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSmartExperienceRetry(object sender, RoutedEventArgs e) => SmartExperienceHost.Retry();

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
        RefreshSmartStatus();

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

    private bool _refreshingAccounts;
    private readonly HashSet<string> _removingAccounts = [];
    private readonly Dictionary<string, bool> _accountStates = [];
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
                    foreach (var profile in _providers.Where(p => p.IsAccount && p.CliId == cli))
                        _accountStates[AccountKey(cli, profile.AccountId)] = accounts.Any(a => a.AccountId == profile.AccountId);
                    foreach (var account in accounts)
                    {
                        var key = AccountKey(cli, account.AccountId);
                        if (_removingAccounts.Contains(key)) continue;
                        _accountStates[key] = true;
                        var profile = _providers.FirstOrDefault(p => p.IsAccount && p.CliId == cli && p.AccountId == account.AccountId);
                        if (profile is null)
                        {
                            profile = new ByokProvider { Id = Guid.NewGuid().ToString("N"), CliId = cli, IsAccount = true, AccountId = account.AccountId,
                                Name = account.Email ?? (account.AccountId == CodaleAccount.UserAccountId ? "Detected CLI account" : "Detected account") };
                            _providers.Add(profile);
                        }
                        profile.Email = account.Email;
                    }
                    WriteSettings(() => AppSettings.SetAccountLabel(cli, accounts.FirstOrDefault(a => a.AccountId == "")?.Email));
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
            await SignInAsync(cli, id);
            var account = await CodaleAccountCli.GetAccountAsync(cli, accountId: id);
            if (!account.SignedIn) { status.Text = "Account setup did not complete. No account was added."; return; }
            if (!_providers.Any(p => p.IsAccount && p.CliId == cli && p.AccountId == id))
                _providers.Add(new ByokProvider { Id = Guid.NewGuid().ToString("N"), Name = account.Email ?? "Connected account", CliId = cli,
                    IsAccount = true, AccountId = id, Email = account.Email });
            _accountStates[AccountKey(cli, id)] = true;
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
            if (provider.AccountId.Length == 0) WriteSettings(() => AppSettings.SetAccountLabel(cli, null));
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

        if (provider.IsAccount)
        {
            var status = new TextBlock { Text = _accountStates.GetValueOrDefault(AccountKey(cli, provider.AccountId), true)
                ? provider.Email ?? "Connected account" : "Not connected. Remove this account and add it again to reconnect.", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(status);
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

        ModelField("Flash model", provider.IsAccount ? "CLI default" : "Select a model", provider.FlashModel, v => provider.FlashModel = v);
        ModelField("Pro model", "Flash model when blank", provider.ProModel, v => provider.ProModel = v);

        if (provider.IsAccount)
        {
            var accountStatus = panel.Children.OfType<TextBlock>().First();
            var removeAccount = new Button { Content = "Remove account" };
            removeAccount.Click += async (_, _) => await RemoveAccountAsync(provider, cli, removeAccount, accountStatus);
            panel.Children.Add(removeAccount);
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
    private static void SetModelStatus(List<ComboBox> boxes, string? message)
    {
        foreach (var box in boxes)
        {
            ToolTipService.SetToolTip(box, message);
        }
    }

    /// <summary>Fills the model dropdowns from the endpoint's OpenAI-style GET /models; failures leave the current list.</summary>
    private async Task LoadModelsAsync(ByokProvider provider, List<ComboBox> boxes)
    {
        if (provider.IsAccount)
        {
            var accountModels = ChatViewModel.KnownCatalogue(provider.CliId, provider.AccountId)
                ?? await ChatViewModel.FetchCatalogueAsync(provider.CliId, provider.AccountId);
            if (accountModels.Count == 0)
            {
                SetModelStatus(boxes, "No models reported for this account. Enter a model ID manually.");
                return;
            }
            SetModelStatus(boxes, null);
            foreach (var box in boxes)
            {
                var current = box.Text;
                box.Items.Clear();
                foreach (var model in accountModels) box.Items.Add(model.Id);
                box.Text = current;
            }
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
        provider.ModelContextWindows ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (model.ContextWindow is int window && window > 0)
            {
                provider.ModelContextWindows[model.Id] = window;
            }
        }
        var ids = models.Select(model => model.Id).ToList();
        SaveProviders();

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

    private void OnGitPollChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(sender.Value))
        {
            return;
        }

        AppSettings.GitPollSeconds = (int)Math.Round(sender.Value);
    }
}
