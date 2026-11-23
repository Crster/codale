using Microsoft.Web.WebView2.Core;

namespace Codale.Browser;

/// <summary>
/// The user's answers to a site's permission requests (notifications, location, camera, microphone, clipboard,
/// sensors), remembered per site and kind so the question is asked once.
/// </summary>
public sealed class SitePermissions
{
    private sealed class Store
    {
        public Dictionary<string, bool> Decisions { get; set; } = [];
    }

    private readonly string _path;
    private readonly Store _store;

    public SitePermissions(string folder)
    {
        _path = Path.Combine(folder, "site-permissions.json");
        _store = JsonFile.Load<Store>(_path);
    }

    /// <summary>The remembered answer for this site and kind, or null when the user has not decided yet.</summary>
    public bool? Get(string origin, CoreWebView2PermissionKind kind) =>
        _store.Decisions.TryGetValue(Key(origin, kind), out var allow) ? allow : null;

    public void Set(string origin, CoreWebView2PermissionKind kind, bool allow)
    {
        _store.Decisions[Key(origin, kind)] = allow;
        JsonFile.Save(_path, _store);
    }

    private static string Key(string origin, CoreWebView2PermissionKind kind) => $"{origin}|{kind}";

    /// <summary>What a permission kind lets a site do, for the prompt; null for kinds the browser does not ask about.</summary>
    public static string? Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Notifications => "show notifications",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.ClipboardRead => "read the clipboard",
        CoreWebView2PermissionKind.OtherSensors => "read motion and light sensors",
        _ => null,
    };
}
