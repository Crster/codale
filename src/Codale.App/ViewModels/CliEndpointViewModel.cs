using System.ComponentModel;

using Codale.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>One row of the status bar's provider picker: Default, or a BYOK provider by name.</summary>
public sealed record ProviderChoice(string Name, string Label, string Detail);

/// <summary>
/// The status bar's provider picker. The providers themselves are configured in the
/// Settings window (settings.json); here the user only chooses which one Claude
/// uses: Default (no BYOK - the Claude CLI login) or one provider.
/// The choice is stored at once and reaches the next spawned session.
/// </summary>
public sealed partial class CliEndpointViewModel : ObservableObject, IDisposable
{
    /// <summary>Set while the list rebuilds, so re-selecting the current row is not a write.</summary>
    private bool _loading;

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
        if (e.PropertyName == nameof(ChatViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(CanSwitch));
            OnPropertyChanged(nameof(RouteText));
        }
        else if (e.PropertyName == nameof(ChatViewModel.EndpointName) && !_loading)
        {
            Reload();
        }
    }

    private ByokProvider? ActiveProvider => AppSettings.FindByok(_chat?.EndpointName);

    /// <summary>False while the chat in front runs a turn: a switch restarts its CLI and would cut the turn off.</summary>
    public bool CanSwitch => _chat is not { IsBusy: true };

    /// <summary>True when a BYOK provider (not Default) is in use.</summary>
    public bool IsActive => ActiveProvider is not null;

    /// <summary>The chat's provider name, or "" for Default.</summary>
    public string ActiveName => ActiveProvider?.Name.Trim() ?? "";

    /// <summary>Where Claude goes with the current choice, in words.</summary>
    public string RouteText => !CanSwitch
        ? "A turn is running. Switch the provider when it finishes."
        : ActiveProvider is not { } provider
            ? "Claude uses your Claude login in this chat."
            : provider.LiteModel.Length == 0 && provider.SmartModel.Length == 0
                ? $"This chat uses {provider.Name.Trim()}. It has no model name yet; add one in settings.json."
                : $"This chat uses {provider.Name.Trim()}.";

    /// <summary>Re-reads the provider list and the current choice; the flyout calls this when it opens.</summary>
    public void Reload()
    {
        _loading = true;

        // Rebuild the rows only when the providers changed: replacing them under the open
        // flyout's ListView drops its selection and focus (typing in Settings fires this per keystroke).
        var wanted = new List<ProviderChoice> { new("", "Default", "Claude CLI login") };
        foreach (var provider in AppSettings.ByokProviders)
        {
            var name = provider.Name.Trim();
            wanted.Add(new ProviderChoice(name, name, provider.BaseUrl.Length > 0 ? provider.BaseUrl : "No base URL set"));
        }

        if (!wanted.SequenceEqual(Choices))
        {
            Choices.ReplaceAll(wanted);
        }

        var active = ActiveName;
        Selected = Choices.FirstOrDefault(c => string.Equals(c.Name, active, StringComparison.OrdinalIgnoreCase)) ?? Choices[0];
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
        // Per chat and never stored: a new chat starts on Default.
        _loading = true;
        try
        {
            if (_chat is not null)
            {
                _ = _chat.SetEndpointAsync(value.Name);
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
