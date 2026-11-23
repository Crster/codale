using System.Security.Cryptography;
using System.Text;

namespace Codale.Browser;

/// <summary>A saved login as the UI sees it. The password is not part of it: it is read on demand with <see cref="CredentialStore.Password"/>.</summary>
public sealed record CredentialEntry(string Id, string Origin, string Username, DateTimeOffset Modified)
{
    public string Site => Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Host : Origin;
    public string UpdatedText => Modified.ToLocalTime().ToString("d MMM yyyy");
}

/// <summary>
/// Saved logins, per site (origin). Passwords are encrypted with DPAPI for the current Windows user, so the
/// file is useless to another account or machine; the site and user name are kept in clear so they can be listed.
/// <see cref="IsNever"/> remembers sites the user chose never to save for.
/// </summary>
public sealed class CredentialStore
{
    private sealed class Login
    {
        public string Id { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Username { get; set; } = "";
        public string Cipher { get; set; } = "";
        public DateTimeOffset Modified { get; set; }
    }

    private sealed class Vault
    {
        public List<Login> Logins { get; set; } = [];
        public List<string> Never { get; set; } = [];
    }

    private readonly string _path;
    private readonly Vault _vault;

    public CredentialStore(string folder)
    {
        _path = Path.Combine(folder, "passwords.json");
        _vault = JsonFile.Load<Vault>(_path);
    }

    /// <summary>Every saved login, ordered by site and user name.</summary>
    public IReadOnlyList<CredentialEntry> All() => _vault.Logins.OrderBy(l => l.Origin, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Username, StringComparer.OrdinalIgnoreCase).Select(Entry).ToList();

    public IReadOnlyList<CredentialEntry> ForOrigin(string origin) => _vault.Logins.Where(l => l.Origin == origin).Select(Entry).ToList();

    public CredentialEntry? Find(string origin, string username) => _vault.Logins.FirstOrDefault(l => l.Origin == origin && l.Username == username) is { } login ? Entry(login) : null;

    /// <summary>The decrypted password, or null when the entry is gone or cannot be decrypted for this user.</summary>
    public string? Password(CredentialEntry entry) => Unseal(_vault.Logins.FirstOrDefault(l => l.Id == entry.Id)?.Cipher);

    /// <summary>Adds a login, or replaces the password of the one already saved for that site and user name.</summary>
    public bool Upsert(string origin, string username, string password)
    {
        var login = _vault.Logins.FirstOrDefault(l => l.Origin == origin && l.Username == username);
        if (login is null)
        {
            _vault.Logins.Add(new Login { Id = Guid.NewGuid().ToString("N"), Origin = origin, Username = username, Cipher = Seal(password), Modified = DateTimeOffset.UtcNow });
        }
        else
        {
            login.Cipher = Seal(password);
            login.Modified = DateTimeOffset.UtcNow;
        }

        return Save();
    }

    public bool Delete(string id)
    {
        _vault.Logins.RemoveAll(l => l.Id == id);
        return Save();
    }

    public bool IsNever(string origin) => _vault.Never.Contains(origin, StringComparer.OrdinalIgnoreCase);

    public bool SetNever(string origin, bool never)
    {
        _vault.Never.RemoveAll(o => o.Equals(origin, StringComparison.OrdinalIgnoreCase));
        if (never) _vault.Never.Add(origin);
        return Save();
    }

    private bool Save() => JsonFile.Save(_path, _vault);

    private static CredentialEntry Entry(Login login) => new(login.Id, login.Origin, login.Username, login.Modified);

    private static string Seal(string text) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    private static string? Unseal(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
