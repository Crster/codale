using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Codale.Db;

namespace Codale.Workspace;

/// <summary>
/// The workspace's secrets. Each value is protected by Windows DPAPI for the current account.
/// </summary>
public sealed partial class EnvStore(string directory, WorkspaceDatabasePool? databases = null)
{
    private sealed record Secret(byte[] ProtectedValue);
    private WorkspaceDatabasePool Pool => databases ?? WorkspaceDb.Shared;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <exception cref="ArgumentException">The name is not a valid environment variable name.</exception>
    public void Set(string name, string value)
    {
        Check(name);
        ArgumentNullException.ThrowIfNull(value);
        Use(db => db.Record<Secret>().Key(name).Value(Protect(name, value)));
    }

    /// <returns>The secret, or null when there is none by that name.</returns>
    public string? Get(string name)
    {
        Check(name);
        return Use(db => db.Record<Secret>().Key(name).Get() is { } secret ? Unprotect(name, secret) : null);
    }

    public bool Remove(string name)
    {
        Check(name);
        return Use(db => db.Record<Secret>().Key(name).TryDelete(out _));
    }

    /// <summary>The names of the stored secrets, sorted. Never their values.</summary>
    public IReadOnlyList<string> Names() => Use(db => db.Record<Secret>().Keys());

    /// <summary>Adds the <c>NAME=value</c> lines of a <c>.env</c> text; comments, blanks and an <c>export</c> prefix are skipped.</summary>
    /// <returns>The names that were stored.</returns>
    public IReadOnlyList<string> Import(string dotEnv)
    {
        var stored = new List<KeyValuePair<string, Secret>>();
        foreach (var raw in dotEnv.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }

            if (NamePattern().IsMatch(name))
            {
                stored.Add(new(name, Protect(name, value)));
            }
        }

        if (stored.Count > 0) Use(db => db.Record<Secret>().PutMany(stored));
        return [.. stored.Select(item => item.Key)];
    }

    private static void Check(string name)
    {
        if (!NamePattern().IsMatch(name ?? ""))
        {
            throw new ArgumentException("A secret's name is letters, digits and underscores, and does not start with a digit.", nameof(name));
        }
    }

    private T Use<T>(Func<CodaleDatabase, T> work) => Pool.UseRecords(directory, work);

    private static Secret Protect(string name, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Workspace secrets require Windows DPAPI.");
        var plaintext = Encoding.UTF8.GetBytes(value);
        try { return new(ProtectedData.Protect(plaintext, Entropy(name), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string Unprotect(string name, Secret secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Workspace secrets require Windows DPAPI.");
        var plaintext = ProtectedData.Unprotect(secret.ProtectedValue, Entropy(name), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("Codale.Workspace.Secret\0" + name);

    private void Use(Action<CodaleDatabase> work) => Use(db =>
    {
        work(db);
        return true;
    });
}
