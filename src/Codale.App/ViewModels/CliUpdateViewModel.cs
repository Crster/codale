using Codale.Agents;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The installed Claude CLI's version and whether a newer one is published. One per
/// app: every window shows the same CLI. The latest release is looked up at most once
/// an hour; the installed version is re-read on every refresh, so it follows an update
/// as soon as it lands.
/// </summary>
public sealed partial class CliUpdateViewModel : ObservableObject
{
    private static readonly TimeSpan LatestTtl = TimeSpan.FromHours(1);

    private Version? _latest;
    private DateTimeOffset _latestChecked;

    public static CliUpdateViewModel Instance { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText), nameof(IsVersionKnown), nameof(UpdateAvailable), nameof(UpdateTip))]
    public partial Version? Installed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable), nameof(UpdateTip))]
    public partial Version? Latest { get; private set; }

    public bool IsVersionKnown => Installed is not null;

    public string VersionText => Installed is { } version ? $"v{version}" : "";

    public bool UpdateAvailable => Installed is { } installed && Latest is { } latest && latest > installed;

    public string UpdateTip => UpdateAvailable
        ? $"Claude CLI {Latest} is available (you have {Installed}). Click to update."
        : "";

    public async Task RefreshAsync(bool forceLatest = false)
    {
        Installed = await CliVersionCheck.InstalledAsync();

        if (Installed is null || (!forceLatest && _latest is not null && DateTimeOffset.Now - _latestChecked < LatestTtl))
        {
            Latest = _latest;
            return;
        }

        if (await CliVersionCheck.LatestAsync() is { } latest)
        {
            _latest = latest;
            _latestChecked = DateTimeOffset.Now;
        }

        Latest = _latest;
    }
}
