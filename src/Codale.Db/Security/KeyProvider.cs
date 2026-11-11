using System.Security.Cryptography;
using System.Text;

namespace Codale.Db;

/// <summary>
/// Protects the database's random 256-bit master key at rest. The master key is generated once, wrapped by
/// the provider and stored in <c>protect.json</c>; it never touches disk in the clear.
/// </summary>
public abstract class KeyProvider
{
    internal KeyProvider() { }

    internal abstract string Kind { get; }
    internal abstract byte[] Wrap(byte[] masterKey, byte[] salt);
    internal abstract byte[] Unwrap(byte[] wrapped, byte[] salt);

    /// <summary>The Windows account that created the database can open it; nobody else, on any other machine.</summary>
    public static KeyProvider Dpapi { get; } = new DpapiKeyProvider();

    /// <summary>A passphrase, stretched with PBKDF2-SHA256 (600k rounds) into a key-encryption key.</summary>
    public static KeyProvider Passphrase(string passphrase) => new PassphraseKeyProvider(passphrase);

    /// <summary>An RSA key in a PEM file (private key needed to open an existing database).</summary>
    public static KeyProvider Pem(Uri file) => new PemKeyProvider(file);
}

internal sealed class PassphraseKeyProvider : KeyProvider
{
    private const int Iterations = 600_000;
    private readonly string _passphrase;

    public PassphraseKeyProvider(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _passphrase = passphrase;
    }

    internal override string Kind => "passphrase";

    internal override byte[] Wrap(byte[] masterKey, byte[] salt) => RecordCipher.Seal(Kek(salt), "master-key", masterKey);

    internal override byte[] Unwrap(byte[] wrapped, byte[] salt) => RecordCipher.Open(Kek(salt), "master-key", wrapped);

    private byte[] Kek(byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

internal sealed class DpapiKeyProvider : KeyProvider
{
    internal override string Kind => "dpapi";

    internal override byte[] Wrap(byte[] masterKey, byte[] salt)
    {
        if (!OperatingSystem.IsWindows()) throw Unsupported();
        return ProtectedData.Protect(masterKey, salt, DataProtectionScope.CurrentUser);
    }

    internal override byte[] Unwrap(byte[] wrapped, byte[] salt)
    {
        if (!OperatingSystem.IsWindows()) throw Unsupported();
        return ProtectedData.Unprotect(wrapped, salt, DataProtectionScope.CurrentUser);
    }

    private static PlatformNotSupportedException Unsupported() => new("DPAPI protection needs Windows; use a passphrase or PEM key instead.");
}

internal sealed class PemKeyProvider : KeyProvider
{
    private readonly string _path;

    public PemKeyProvider(Uri file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.IsFile) throw new ArgumentException("The PEM key must be a file: URI.", nameof(file));
        _path = file.LocalPath;
    }

    internal override string Kind => "pem";

    internal override byte[] Wrap(byte[] masterKey, byte[] salt) => Load().Encrypt(masterKey, RSAEncryptionPadding.OaepSHA256);

    internal override byte[] Unwrap(byte[] wrapped, byte[] salt) => Load().Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);

    private RSA Load()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_path));
        return rsa;
    }
}
