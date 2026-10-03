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

    /// <summary>True when a BYOK provider (not Default) is in use.</summary>
    public bool IsActive => AppSettings.ActiveByok is not null;

    /// <summary>The selected provider's name, or "" for Default.</summary>
    public string ActiveName => AppSettings.ActiveByok?.Name.Trim() ?? "";

    /// <summary>Where Claude goes with the current choice, in words.</summary>
    public string RouteText => AppSettings.ActiveByok is not { } provider
        ? "Claude uses your Claude login."
        : provider.Model.Length == 0 && provider.SmartModel.Length == 0
            ? $"Claude uses {provider.Name.Trim()}. It has no model name yet; add one in settings.json."
            : $"Claude uses {provider.Name.Trim()}. Applies to sessions started from now on.";

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

        // Our own write raises Changed; rebuilding the list from inside the ListView's selection change is what to avoid.
        _loading = true;
        try
        {
            AppSettings.ByokSelected = value.Name;
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

    public void Dispose() => AppSettings.Changed -= OnSettingsChanged;
}
