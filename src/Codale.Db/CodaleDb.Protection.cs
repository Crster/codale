using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codale.Db;

/// <summary>Per-value encryption. A random master key is wrapped by a <see cref="KeyProvider"/> and kept in <c>protect.json</c>.</summary>
public sealed partial class CodaleDb
{
    private byte[]? _masterKey;

    private sealed record KeyFile(string Kind, string Salt, string Wrapped);

    /// <summary>Protects <c>WriteProtect</c>/<c>ReadProtect</c> values with a passphrase.</summary>
    public void Protect(string passphrase) => Protect(KeyProvider.Passphrase(passphrase));

    /// <summary>Protects values with the RSA key in a PEM file.</summary>
    public void Protect(Uri pemFile) => Protect(KeyProvider.Pem(pemFile));

    /// <summary>
    /// Unlocks (or, the first time, creates) the master key using <paramref name="provider"/>.
    /// A database keeps the provider kind it was first protected with.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Wrong passphrase / key.</exception>
    public void Protect(KeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var file = Path.Combine(_dir, "protect.json");
        byte[] master;
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(file)) ?? throw new InvalidDataException("protect.json is empty.");
            if (stored.Kind != provider.Kind) throw new InvalidOperationException($"This database is protected with '{stored.Kind}', not '{provider.Kind}'.");
            try
            {
                master = provider.Unwrap(Convert.FromBase64String(stored.Wrapped), Convert.FromBase64String(stored.Salt));
            }
            catch (CryptographicException ex)
            {
                throw new UnauthorizedAccessException("The key does not unlock this database.", ex);
            }
        }
        else
        {
            master = RandomNumberGenerator.GetBytes(32);
            var salt = RandomNumberGenerator.GetBytes(16);
            var wrapped = provider.Wrap(master, salt);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new KeyFile(provider.Kind, Convert.ToBase64String(salt), Convert.ToBase64String(wrapped))));
            File.Move(temp, file, overwrite: true);
        }
        if (_masterKey is not null) CryptographicOperations.ZeroMemory(_masterKey);
        _masterKey = master;
    }

    internal void WriteProtected(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = _masterKey ?? throw new InvalidOperationException("Call db.Protect(...) before WriteProtect().");
        WriteBytes(path, RecordCipher.Seal(key, path, Encoding.UTF8.GetBytes(value)), ValueFlags.Protected, null);
    }

    internal string? ReadProtected(string path)
    {
        var key = _masterKey ?? throw new InvalidOperationException("Call db.Protect(...) before ReadProtect().");
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false);
            if (value is not { HasValue: true }) return null;
            if ((value.ValueFlags & ValueFlags.Protected) == 0) throw new InvalidOperationException("This value was not written with WriteProtect().");
            return Encoding.UTF8.GetString(RecordCipher.Open(key, path, ReadRaw(value)));
        }
        finally { _lock.ExitReadLock(); }
    }
}
