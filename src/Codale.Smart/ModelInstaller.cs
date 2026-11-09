using System.Security.Cryptography;

namespace Codale.Smart;

public sealed class ModelInstaller(string directory, HttpClient? client = null)
{
    public const string Revision = "cd0c74c575b5c416f3011b5b4c298077f26ca8c2";
    public const string FileName = "LFM3-1.7B-Q4_K_M.gguf";
    public const string Sha256 = "3793e1e6e2266919d6e015287221b41100175a98eddaba92e111aed3b05c93c4";
    public const long Size = 1099627808;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly long _size = Size;
    private readonly string _hash = Sha256;
    internal ModelInstaller(string directory, HttpClient client, long size, string hash) : this(directory, client)
    { _size = size; _hash = hash; }
    public string ModelPath => Path.Combine(directory, Revision, FileName);

    public async Task<string> EnsureAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        // Also serializes downloads made by separate Codale project processes.
        await using var lease = await AcquireAsync(ModelPath + ".lock", ct);
        if (File.Exists(ModelPath) && await VerifyFileAsync(ModelPath, _size, _hash, ct)) return ModelPath;
        var partial = ModelPath + ".partial";
        try
        {
            using var response = await (client ?? Http).GetAsync(
                $"https://huggingface.co/LiquidAI/LFM3-1.7B-GGUF/resolve/{Revision}/{FileName}", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > _size) throw new InvalidDataException("Model download exceeded the expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)total / _size);
                }
            }
            if (!await VerifyFileAsync(partial, _size, _hash, ct)) throw new InvalidDataException("Model checksum mismatch. Retry the download.");
            File.Move(partial, ModelPath, true);
            return ModelPath;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(200, ct); }
        }
    }
    public static Task<bool> VerifyAsync(string path, CancellationToken ct) => VerifyFileAsync(path, Size, Sha256, ct);
    private static async Task<bool> VerifyFileAsync(string path, long size, string hash, CancellationToken ct)
    {
        if (new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
