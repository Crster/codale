using System.Security.Cryptography;
using System.Text;

namespace Codale.Db;

/// <summary>AES-256-GCM sealing of one value. Layout: <c>nonce(12) | tag(16) | ciphertext</c>; the path is authenticated data.</summary>
internal static class RecordCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Seal(byte[] key, string path, ReadOnlySpan<byte> plain)
    {
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(path));
        return output;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered data, or ciphertext moved to another path.</exception>
    public static byte[] Open(byte[] key, string path, ReadOnlySpan<byte> sealedValue)
    {
        if (sealedValue.Length < NonceSize + TagSize) throw new CryptographicException("Protected value is truncated.");
        var plain = new byte[sealedValue.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedValue[..NonceSize], sealedValue[(NonceSize + TagSize)..], sealedValue.Slice(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(path));
        return plain;
    }
}
