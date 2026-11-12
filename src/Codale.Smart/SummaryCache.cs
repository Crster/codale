using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codale.Smart;

/// <summary>Content-addressed, per-worktree summaries; callers never use a stale entry.</summary>
public sealed class SummaryCache(string directory, string? modelIdentity = null)
{
    public const int PromptVersion = 2;
    private sealed record Entry(string Path, string Hash, string Model, int Prompt, string Summary,
        bool Thinking, IReadOnlyList<SmartEvidence>? Dependencies);
    private string CachePath(string worktree, string path) => Path.Combine(directory,
        Hash(Path.GetFullPath(worktree).ToUpperInvariant()), Hash(path.ToUpperInvariant()) + ".json");
    public static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public string? Get(string worktree, string path, string contentHash, bool thinking = false)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(CachePath(worktree, path)));
            return entry is not null && entry.Hash == contentHash && entry.Model == (modelIdentity ?? ModelInstaller.Revision) && entry.Prompt == PromptVersion && entry.Thinking == thinking
                && (entry.Dependencies ?? []).All(Fresh)
                ? entry.Summary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static bool Fresh(SmartEvidence evidence)
    {
        if (evidence.Kind != "file") return true;
        try
        {
            using var stream = File.OpenRead(evidence.Path);
            return stream.Length <= 16 * 1024 * 1024 && Convert.ToHexString(SHA256.HashData(stream)) == evidence.Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Put(string worktree, string path, string contentHash, string summary, bool thinking = false, IReadOnlyList<SmartEvidence>? dependencies = null)
    {
        var dest = CachePath(worktree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(path, contentHash, modelIdentity ?? ModelInstaller.Revision, PromptVersion, summary, thinking, dependencies?.Where(d => d.Kind == "file").ToArray())));
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
