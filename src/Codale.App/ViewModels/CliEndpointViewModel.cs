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

    /// <summary>Where Claude goes with the current choice, in words.</summary>
    public string RouteText => !CanSwitch
        ? "A turn is running. Switch the provider when it finishes."
        : ActiveProvider is not { IsAccount: false } provider
            ? $"This chat uses Codale's {_chat?.ProviderName} account."
            : provider.FlashModel.Length == 0 && provider.ProModel.Length == 0
                ? $"This chat uses {provider.Name.Trim()}. It has no model name yet; add one in settings.json."
                : $"This chat uses {provider.Name.Trim()}.";

    /// <summary>Re-reads the provider list and the current choice; the flyout calls this when it opens.</summary>
    public void Reload()
    {
        _loading = true;

        // Rebuild the rows only when the providers changed: replacing them under the open
        // flyout's ListView drops its selection and focus (typing in Settings fires this per keystroke).
        var wanted = new List<ProviderChoice>();
        foreach (var cli in new[] { "claude", "codex" })
        {
            var label = cli == "codex" ? "Codex" : "Claude";
            wanted.Add(new("", label + ": " + AppSettings.AccountLabel(cli), "Sign-in account", cli));
            foreach (var provider in AppSettings.ByokProviders.Where(p => p.Supports(cli)))
            {
                var name = provider.Name.Trim();
                var detail = provider.IsAccount ? "Sign-in account" : cli == "codex" ? provider.BaseUrl : provider.AnthropicBaseUrl;
                wanted.Add(new(provider.Key, label + ": " + (provider.IsAccount ? provider.Email ?? name : name), detail, cli));
            }
        }

        if (!wanted.SequenceEqual(Choices))
        {
            Choices.ReplaceAll(wanted);
        }

        Selected = Choices.FirstOrDefault(c => c.CliId == (_chat?.CliId ?? "claude") && string.Equals(c.Name, _chat?.EndpointName ?? "", StringComparison.OrdinalIgnoreCase)) ?? Choices[0];
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