using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codale.Smart;

/// <summary>Content-addressed, per-worktree summaries; callers never use a stale entry.</summary>
public sealed class SummaryCache(string directory)
{
    public const int PromptVersion = 1;
    private sealed record Entry(string Path, string Hash, string Model, int Prompt, string Summary);
    private string CachePath(string worktree, string path) => Path.Combine(directory,
        Hash(Path.GetFullPath(worktree).ToUpperInvariant()), Hash(path.ToUpperInvariant()) + ".json");
    public static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public string? Get(string worktree, string path, string contentHash)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(CachePath(worktree, path)));
            return entry is not null && entry.Hash == contentHash && entry.Model == ModelInstaller.Revision && entry.Prompt == PromptVersion
                ? entry.Summary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Put(string worktree, string path, string contentHash, string summary)
    {
        var dest = CachePath(worktree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(path, contentHash, ModelInstaller.Revision, PromptVersion, summary)));
            File.Move(temp, dest, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Prune(string worktree, IReadOnlySet<string> presentPaths)
    {
        var folder = Path.GetDirectoryName(CachePath(worktree, "_"))!;
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                if (entry is null || !presentPaths.Contains(entry.Path)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }
}
