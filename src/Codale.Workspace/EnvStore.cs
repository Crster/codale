using System.Text.RegularExpressions;

using Codale.Db;

namespace Codale.Workspace;

/// <summary>
/// The workspace's secrets (API keys, connection strings, tokens). Every value is encrypted on its own with
/// AES-256-GCM under a random master key that Windows DPAPI wraps for the signed-in account, so the database
/// folder is useless on another machine or account. Names are not secret and can be listed; values are only
/// returned by <see cref="Get"/>.
/// </summary>
/// <remarks>Layout: <c>env.NAME</c> holds the encrypted value.</remarks>
public sealed partial class EnvStore(string directory)
{
    /// <summary>Protecting swaps the database's master key: one caller at a time.</summary>
    private static readonly object KeyGate = new();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <exception cref="ArgumentException">The name is not a valid environment variable name.</exception>
    public void Set(string name, string value)
    {
        Check(name);
        ArgumentNullException.ThrowIfNull(value);
        Use(db => db.Open("env").Open(name).WriteProtect(value));
    }

    /// <returns>The secret, or null when there is none by that name.</returns>
    public string? Get(string name)
    {
        Check(name);
        return Use(db => db.Open("env").Open(name).ReadProtect());
    }

    public bool Remove(string name)
    {
        Check(name);
        return WorkspaceDb.Use(directory, db => db.Open("env").Open(name).Remove());
    }

    /// <summary>The names of the stored secrets, sorted. Never their values.</summary>
    public IReadOnlyList<string> Names() => WorkspaceDb.Use(directory, db => db.Open("env").Children());

    /// <summary>Adds the <c>NAME=value</c> lines of a <c>.env</c> text; comments, blanks and an <c>export</c> prefix are skipped.</summary>
    /// <returns>The names that were stored.</returns>
    public IReadOnlyList<string> Import(string dotEnv)
    {
        var stored = new List<string>();
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
                Set(name, value);
                stored.Add(name);
            }
        }

        return stored;
    }

    private static void Check(string name)
    {
        if (!NamePattern().IsMatch(name ?? ""))
        {
            throw new ArgumentException("A secret's name is letters, digits and underscores, and does not start with a digit.", nameof(name));
        }
    }

    private T Use<T>(Func<CodaleDb, T> work)
    {
        lock (KeyGate)
        {
            return WorkspaceDb.Use(directory, db =>
            {
                db.Protect(KeyProvider.Dpapi);
                return work(db);
            });
        }
    }

    private void Use(Action<CodaleDb> work) => Use<object?>(db =>
    {
        work(db);
        return null;
    });
}
