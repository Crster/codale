using System.Runtime.InteropServices;
using System.Text;

namespace Codale.App.Services;

/// <summary>
/// Wraps a secret (a BYOK API key) for storage in settings.json with the Windows Data
/// Protection API, CurrentUser scope: only the same Windows account can read the blob
/// back, so a copied or synced settings file does not leak the key. Stored values are
/// <c>enc:</c> plus base64; anything without that prefix is a legacy plaintext value.
/// </summary>
/// <remarks>
/// Calls crypt32 directly: the managed ProtectedData class ships as a separate package.
/// </remarks>
internal static class SecretProtector
{
    private const string Prefix = "enc:";
    private const uint UiForbidden = 0x1;

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts <paramref name="secret"/>; empty stays empty, and a failure returns the plaintext (logged) so the key is never lost.</summary>
    public static string Protect(string secret)
    {
        if (secret.Length == 0 || IsProtected(secret))
        {
            return secret;
        }

        try
        {
            return Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), protect: true));
        }
        catch (Exception ex)
        {
            CrashLog.Error("settings", "could not protect a stored secret; writing it unprotected", ex);
            return secret;
        }
    }

    /// <summary>Reverses <see cref="Protect"/>. Plaintext (legacy) passes through; a blob from another account or machine yields "".</summary>
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }

        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false));
        }
        catch (Exception ex)
        {
            CrashLog.Warn("settings", $"a stored secret could not be decrypted (written by another account or machine?): {ex.Message}");
            return "";
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inBlob.Data, input.Length);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.Data);
            if (outBlob.Data != IntPtr.Zero)
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
