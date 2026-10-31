using Codale.Agents;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// An agent CLI's version and whether a newer one is published. One per
/// page, following whichever CLI its panel shows. The latest release is looked up at most once
/// an hour; the installed version is re-read on every refresh, so it follows an update
/// as soon as it lands.
/// </summary>
public sealed partial class CliUpdateViewModel : ObservableObject
{
    private static readonly TimeSpan LatestTtl = TimeSpan.FromHours(1);

    private static readonly Dictionary<string, (Version Version, DateTimeOffset Checked)> LatestByCli = new();

    private CliDescriptor _cli = CliLocator.ActiveCli;
    private int _refreshGeneration;

    public CliDescriptor Cli => _cli;

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
        ? $"{_cli.DisplayName} CLI {Latest} is available (you have {Installed}). Click to update."
        : "";

    public async Task RefreshAsync(CliDescriptor cli, bool forceLatest = false)
    {
        var generation = ++_refreshGeneration;
        _cli = cli;
        Installed = null;
        Latest = null;

        var installed = await CliVersionCheck.InstalledAsync(cli);
        if (generation != _refreshGeneration)
        {
            return;
        }

        Installed = installed;
        if (installed is null || CliVersionCheck.UpdateCommand(cli) is null)
        {
            return;
        }

        (Version Version, DateTimeOffset Checked) cached;
        lock (LatestByCli)
        {
            LatestByCli.TryGetValue(cli.Id, out cached);
        }

        if (forceLatest || cached.Version is null || DateTimeOffset.Now - cached.Checked >= LatestTtl)
        {
            if (await CliVersionCheck.LatestAsync(cli) is { } latest)
            {
                cached = (latest, DateTimeOffset.Now);
                lock (LatestByCli)
                {
                    LatestByCli[cli.Id] = cached;
                }
            }
        }

        if (generation == _refreshGeneration)
        {
            Latest = cached.Version;
        }
    }
}
