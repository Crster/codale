namespace Codale.Browser;

/// <summary>A site that asked for a passkey: the last ceremony, how many, and when.</summary>
public sealed record PasskeySite(string Origin, string LastOperation, int Count, DateTimeOffset LastUsed)
{
    public string Site => Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Host : Origin;

    public string Summary => LastOperation == "create" ? "Passkey created" : "Signed in with a passkey";

    public string LastUsedText => LastUsed.ToLocalTime().ToString("d MMM yyyy, HH:mm");
}

/// <summary>
/// The sites that have asked for a passkey in this browser. Passkeys themselves live in Windows (Windows Hello
/// and the platform authenticator), where WebView2 hands the ceremony; this list is the browser's own record of
/// which sites used one, so it can be reviewed and forgotten from the Passwords and passkeys window.
/// </summary>
public sealed class PasskeySites
{
    private sealed class Store
    {
        public List<PasskeySite> Sites { get; set; } = [];
    }

    private readonly string _path;
    private readonly Store _store;

    public PasskeySites(string folder)
    {
        _path = Path.Combine(folder, "passkey-sites.json");
        _store = JsonFile.Load<Store>(_path);
    }

    public IReadOnlyList<PasskeySite> All() => _store.Sites.OrderByDescending(s => s.LastUsed).ToList();

    public void Record(string origin, string operation)
    {
        var index = _store.Sites.FindIndex(s => s.Origin == origin);
        var previous = index >= 0 ? _store.Sites[index] : new PasskeySite(origin, operation, 0, DateTimeOffset.UtcNow);
        var updated = previous with { LastOperation = operation, Count = previous.Count + 1, LastUsed = DateTimeOffset.UtcNow };
        if (index >= 0) _store.Sites[index] = updated; else _store.Sites.Add(updated);
        JsonFile.Save(_path, _store);
    }

    public void Forget(string origin)
    {
        _store.Sites.RemoveAll(s => s.Origin == origin);
        JsonFile.Save(_path, _store);
    }
}
