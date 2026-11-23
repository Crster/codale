using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials.UI;
using Windows.System;

namespace Codale.Browser;

/// <summary>
/// The Passwords and passkeys window: saved logins (search, fill on the current page, show or copy after
/// Windows Hello, change, delete) and the sites that have used a passkey. Showing or copying a password is
/// always gated on the user's own verification.
/// </summary>
public sealed partial class CredentialsWindow : Window
{
    private readonly CredentialStore _store;
    private readonly PasskeySites _passkeys;
    private readonly Func<CredentialEntry, Task>? _fill;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private string _query = "";

    public CredentialsWindow(CredentialStore store, PasskeySites passkeys, Func<CredentialEntry, Task>? fill, bool showPasskeys)
    {
        InitializeComponent();
        _store = store;
        _passkeys = passkeys;
        _fill = fill;
        Title = "Passwords and passkeys — Codale Browser";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 620));
        FillButton.IsEnabled = fill is not null;

        // A revealed password hides itself after 30 seconds, or sooner when the window closes.
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(30);
        _hideTimer.Tick += (_, _) => HideRevealed();
        Closed += (_, _) =>
        {
            _hideTimer.Stop();
            HideRevealed();
        };

        Refresh();
        ShowSection(showPasskeys);
    }

    // ---- Sections and lists ----------------------------------------------------------

    private void Section_Click(object sender, RoutedEventArgs e) => ShowSection(ReferenceEquals(sender, PasskeysToggle));

    private void ShowSection(bool passkeys)
    {
        PasswordsToggle.IsChecked = !passkeys;
        PasskeysToggle.IsChecked = passkeys;
        PasswordList.Visibility = passkeys ? Visibility.Collapsed : Visibility.Visible;
        PasskeyList.Visibility = passkeys ? Visibility.Visible : Visibility.Collapsed;
        PasswordActions.Visibility = passkeys ? Visibility.Collapsed : Visibility.Visible;
        PasskeyActions.Visibility = passkeys ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = passkeys ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.PlaceholderText = passkeys ? "Search by site" : "Search by site or user name";
        HideRevealed();
        if (passkeys) _ = ShowPasskeyStatusAsync();
        else StatusText.Text = $"{_store.All().Count} saved. Passwords are encrypted for this Windows account; showing or copying one asks you to verify with Windows Hello.";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        Refresh();
    }

    private void Refresh()
    {
        var logins = _store.All().Where(l => Matches(l.Site, l.Username)).ToList();
        PasswordList.ItemsSource = logins;
        PasskeyList.ItemsSource = _passkeys.All().Where(p => Matches(p.Site)).ToList();
        UpdateButtons();
    }

    private bool Matches(params string[] fields) =>
        _query.Length == 0 || fields.Any(f => f.Contains(_query, StringComparison.OrdinalIgnoreCase));

    private CredentialEntry? Selected() => PasswordList.SelectedItem as CredentialEntry;

    private void PasswordList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HideRevealed();
        UpdateButtons();
    }

    private void PasskeyList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var hasLogin = Selected() is not null;
        FillButton.IsEnabled = hasLogin && _fill is not null;
        RevealButton.IsEnabled = hasLogin;
        CopyButton.IsEnabled = hasLogin;
        EditButton.IsEnabled = hasLogin;
        DeleteButton.IsEnabled = hasLogin;
        var hasSite = PasskeyList.SelectedItem is PasskeySite;
        foreach (var button in PasskeyActions.Children.OfType<Button>()) button.IsEnabled = hasSite;
    }

    // ---- Logins -------------------------------------------------------------------------

    private async void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } entry || _fill is null) return;
        await _fill(entry);
        StatusText.Text = $"Filled {entry.Username} on the current page, if it has a sign-in form for {entry.Site}.";
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry is null) return;
        if (!await VerifyAsync($"Show the saved password for {entry.Site}")) return;
        var secret = _store.Password(entry);
        if (secret is null)
        {
            StatusText.Text = "That password could not be read for this Windows account.";
            return;
        }

        RevealedBox.Text = secret;
        RevealedBox.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        _hideTimer.Start();
        StatusText.Text = "Showing the password for 30 seconds.";
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry is null) return;
        if (!await VerifyAsync($"Copy the saved password for {entry.Site}")) return;
        var secret = _store.Password(entry);
        if (secret is null)
        {
            StatusText.Text = "That password could not be read for this Windows account.";
            return;
        }

        var package = new DataPackage();
        package.SetText(secret);
        Clipboard.SetContent(package);
        StatusText.Text = "Password copied. It stays on the clipboard until something else is copied.";
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry is null) return;
        var answer = await Prompts.CredentialAsync(Root.XamlRoot, "Change password", entry.Origin, entry.Username, fixedSite: true);
        if (answer is not { } login) return;
        if (!_store.Upsert(login.Origin, login.Username, login.Password)) StatusText.Text = "The password could not be saved to disk.";
        else StatusText.Text = $"Changed the password for {entry.Username} on {entry.Site}.";
        Refresh();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var answer = await Prompts.CredentialAsync(Root.XamlRoot, "Add password");
        if (answer is not { } login) return;
        if (!_store.Upsert(login.Origin, login.Username, login.Password)) StatusText.Text = "The password could not be saved to disk.";
        else StatusText.Text = $"Saved the password for {login.Username} on {Prompts.Site(login.Origin)}.";
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry is null) return;
        var confirmed = await Prompts.ConfirmAsync(Root.XamlRoot, "Delete this password?", $"The saved password for {entry.Username} on {entry.Site} is removed from this PC. The site itself is not changed.", "Delete");
        if (!confirmed) return;
        _store.Delete(entry.Id);
        HideRevealed();
        StatusText.Text = $"Deleted the saved password for {entry.Username} on {entry.Site}.";
        Refresh();
    }

    private void HideRevealed()
    {
        _hideTimer.Stop();
        RevealedBox.Text = "";
        RevealedBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>Windows Hello for the user's own confirmation; when Hello is not set up on this PC, an explicit confirmation instead.</summary>
    private async Task<bool> VerifyAsync(string message)
    {
        var availability = await UserConsentVerifier.CheckAvailabilityAsync();
        if (availability != UserConsentVerifierAvailability.Available)
        {
            return await Prompts.ConfirmAsync(Root.XamlRoot, "Confirm it is you", "Windows Hello is not set up on this PC, so confirm here instead. " + message + ".", "Continue");
        }

        var result = await UserConsentVerifier.RequestVerificationAsync(message);
        return result == UserConsentVerificationResult.Verified;
    }

    // ---- Passkeys -----------------------------------------------------------------------

    private async Task ShowPasskeyStatusAsync()
    {
        var availability = await UserConsentVerifier.CheckAvailabilityAsync();
        var hello = availability == UserConsentVerifierAvailability.Available ? "Windows Hello is available on this PC."
            : "Windows Hello is not set up on this PC, so passkeys here need a security key or a phone.";
        StatusText.Text = "Passkeys are created and used through Windows, not stored in the browser. This list shows the sites that asked for one in Codale Browser. "
            + hello;
    }

    private async void WindowsOptions_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri("ms-settings:signinoptions"));

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (PasskeyList.SelectedItem is not PasskeySite site) return;
        _passkeys.Forget(site.Origin);
        StatusText.Text = $"Forgot {site.Site}. Its passkeys are still in Windows; use Windows sign-in options to remove them.";
        Refresh();
    }
}
