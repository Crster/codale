using System.ComponentModel;

using Codale.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>One account or compatible endpoint in the combined agent profile picker.</summary>
public sealed record ProviderChoice(string Name, string Label, string Detail, string CliId = "claude");

/// <summary>
/// Chooses the CLI and its account or endpoint for the active chat. Profiles are
/// configured per CLI in Settings; the most recent choice is also the new-chat default.
/// </summary>
public sealed partial class CliEndpointViewModel : ObservableObject, IDisposable
{
    /// <summary>Set while the list rebuilds, so re-selecting the current row is not a write.</summary>
    private bool _loading;
    private bool _switching;

    public CliEndpointViewModel()
    {
        Reload();
        AppSettings.Changed += OnSettingsChanged;
    }

    public RangeObservableCollection<ProviderChoice> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarInitials))]
    public partial ProviderChoice? Selected { get; set; }

    /// <summary>The chat in front, whose provider the picker shows and changes; null before one is attached.</summary>
    private ChatViewModel? _chat;

    /// <summary>Follows the chat in front: the picker shows its provider and switches only that chat.</summary>
    public void Attach(ChatViewModel chat)
    {
        if (ReferenceEquals(_chat, chat))
        {
            return;
        }

        if (_chat is not null)
        {
            _chat.PropertyChanged -= OnChatPropertyChanged;
        }

        _chat = chat;
        chat.PropertyChanged += OnChatPropertyChanged;
        Reload();
        OnPropertyChanged(nameof(CanSwitch));
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsBusy) or nameof(ChatViewModel.IsConnecting))
        {
            OnPropertyChanged(nameof(CanSwitch));
            OnPropertyChanged(nameof(RouteText));
        }
        else if (e.PropertyName is nameof(ChatViewModel.EndpointName) or nameof(ChatViewModel.CliId) && !_loading)
        {
            Reload();
        }
    }

    private ByokProvider? ActiveProvider => AppSettings.FindByok(_chat?.EndpointName);

    /// <summary>False while the chat in front runs a turn (a switch restarts its CLI and would cut the turn off), and for a terminal CLI's session, which has no provider of its own to switch.</summary>
    public bool CanSwitch => !_switching && _chat is not { IsBusy: true } and not { IsTerminalSession: true } and not { IsConnecting: true };

    /// <summary>True when a BYOK provider (not Default) is in use.</summary>
    public bool IsActive => ActiveProvider is { IsAccount: false };

    /// <summary>The chat's provider name, or "" for Default.</summary>
    public string ActiveName => Selected?.Label ?? "Choose profile";

    /// <summary>The account name or email initials; CLI profiles do not expose a profile photo.</summary>
    public string AvatarInitials
    {
        get
        {
            var label = Selected?.Label ?? "";
            var separator = label.IndexOf(':');
            var identity = (separator >= 0 ? label[(separator + 1)..] : label).Trim();
            if (identity.Length == 0 || identity.Equals("Sign-in account", StringComparison.OrdinalIgnoreCase))
                identity = Selected?.CliId ?? "Agent";
            var at = identity.IndexOf('@');
            if (at >= 0) identity = identity[..at];
            var words = identity.Split([' ', '.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
                .Where(word => word.Length > 0).ToArray();
            if (words.Length == 0) return "A";
            return (words.Length > 1 ? $"{words[0][0]}{words[^1][0]}" : words[0][..Math.Min(2, words[0].Length)])
                .ToUpperInvariant();
        }
    }

    /// <summary>Where Claude goes with the current choice, in words.</summary>
    public string RouteText => !CanSwitch
        ? "A turn is running. Switch the provider when it finishes."
        : ActiveProvider is not { IsAccount: false } provider
            ? $"This chat uses Codale's {_chat?.ProviderName} account."
            : provider.FlashModel.Length == 0 && provider.ProModel.Length == 0
                ? $"This chat uses {provider.Name.Trim()}. It has no model name yet; add one in settings.json."
                : $"This chat uses {provider.Name.Trim()}.";

    /// <summary>The agent profile rows shown in the chat's dropdown, plus aliases from folded profile names to their row.</summary>
    public static (List<ProviderChoice> Rows, Dictionary<(string Cli, string Name), ProviderChoice> Aliases) BuildChoices()
    {
        var wanted = new List<ProviderChoice>();
        var aliases = new Dictionary<(string Cli, string Name), ProviderChoice>();
        foreach (var cli in new[] { "claude", "codex" })
        {
            var label = cli == "codex" ? "Codex" : "Claude";
            var profiles = AppSettings.ByokProviders.Where(p => p.Supports(cli)).ToList();
            var accounts = profiles.Where(p => p.IsAccount).ToList();
            var defaultEmail = AppSettings.AccountLabel(cli) is { } saved && saved != "Sign-in account"
                ? saved
                : accounts.FirstOrDefault(p => p.AccountId.Length == 0)?.Email;

            ProviderChoice? defaultRow = null;
            if (defaultEmail is not null || accounts.Count == 0)
            {
                defaultRow = new("", label + ": " + (defaultEmail ?? "Sign-in account"), "Sign-in account", cli);
                wanted.Add(defaultRow);
            }

            foreach (var provider in profiles)
            {
                var name = provider.Name.Trim();
                if (provider.IsAccount && defaultRow is not null && (provider.AccountId.Length == 0
                    || string.Equals(provider.Email, defaultEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    aliases[(cli, provider.Key.ToLowerInvariant())] = defaultRow;
                    continue;
                }

                var detail = provider.IsAccount ? "Sign-in account" : cli == "codex" ? provider.BaseUrl : provider.AnthropicBaseUrl;
                var row = new ProviderChoice(provider.Key, label + ": " + (provider.IsAccount ? provider.Email ?? name : name), detail, cli);
                wanted.Add(row);
                if (provider.IsAccount && defaultRow is null)
                {
                    // No separate default login row: the chat's default ("") shows as the first account.
                    defaultRow = row;
                    aliases[(cli, "")] = row;
                }
            }
        }
        return (wanted, aliases);
    }

    /// <summary>Re-reads the provider list and the current choice; the flyout calls this when it opens.</summary>
    public void Reload()
    {
        _loading = true;

        // Rebuild the rows only when the providers changed: replacing them under the open
        // flyout's ListView drops its selection and focus (typing in Settings fires this per keystroke).
        // One row per real login: the CLI's default login ("") and an account profile for that same
        // login (AccountId "", or the same email) are one account, so they share a row; the aliases
        // map the folded profile names back to it for the selection.
        var (wanted, aliases) = BuildChoices();

        if (!wanted.SequenceEqual(Choices))
        {
            Choices.ReplaceAll(wanted);
        }

        var chatCli = _chat?.CliId ?? "claude";
        var chatName = _chat?.EndpointName ?? "";
        var match = Choices.FirstOrDefault(c => c.CliId == chatCli && string.Equals(c.Name, chatName, StringComparison.OrdinalIgnoreCase));
        if (match is null && aliases.TryGetValue((chatCli, chatName.ToLowerInvariant()), out var alias))
        {
            match = Choices.FirstOrDefault(c => c == alias);
        }

        Selected = match ?? Choices.FirstOrDefault(c => c.CliId == chatCli) ?? Choices[0];
        _loading = false;

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(RouteText));
    }

    partial void OnSelectedChanged(ProviderChoice? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        if (!CanSwitch)
        {
            Reload();
            return;
        }

        // Our own write raises Changed; rebuilding the list from inside the ListView's selection change is what to avoid.
        // Switch only this chat; other open conversations retain their profiles.
        _loading = true;
        try
        {
            if (_chat is not null)
            {
                _ = SwitchAsync(value);
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(RouteText));
    }

    private async Task SwitchAsync(ProviderChoice choice)
    {
        _switching = true;
        OnPropertyChanged(nameof(CanSwitch));
        try
        {
            if (_chat is not null)
            {
                await _chat.SetProfileAsync(choice.CliId, choice.Name);
                if (_chat.CliId == choice.CliId && _chat.EndpointName == choice.Name)
                    AppSettings.RememberProfile(choice.CliId, choice.Name);
            }
        }
        finally
        {
            _switching = false;
            Reload();
            OnPropertyChanged(nameof(CanSwitch));
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_loading)
        {
            Reload();
        }
    }

    public void Dispose()
    {
        AppSettings.Changed -= OnSettingsChanged;
        if (_chat is not null)
        {
            _chat.PropertyChanged -= OnChatPropertyChanged;
        }
    }
}