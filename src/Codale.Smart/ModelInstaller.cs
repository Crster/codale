using System.Security.Cryptography;

namespace Codale.Smart;

public sealed class ModelInstaller
{
    public const string Revision = "93f3f6a8938ee922b784cf4e5b4203cd3428df8f";
    public const string FileName = "granite-4.2-8b-Q4_K_M.gguf";
    public const string Sha256 = "16a9369d0805f80b7377d25d87f937a90c05dc04ad79173a52001e42c9aab311";
    public const long Size = 5347917952;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _directory;
    private readonly HttpClient? _client;
    private readonly SmartModel _model;
    private readonly long _size;
    private readonly string _hash;
    public ModelInstaller(string directory, HttpClient? client = null) : this(directory, SmartModels.Default, client) { }
    public ModelInstaller(string directory, SmartModel model, HttpClient? client = null)
    { _directory = directory; _model = model; _client = client; _size = model.Size; _hash = model.Sha256; }
    internal ModelInstaller(string directory, HttpClient client, long size, string hash) : this(directory, client)
    { _size = size; _hash = hash; }
    public string ModelPath => Path.Combine(_directory, _model.Revision, _model.FileName);

    public async Task<string> EnsureAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        // Also serializes downloads made by separate Codale project processes.
        await using var lease = await AcquireAsync(ModelPath + ".lock", ct);
        if (File.Exists(ModelPath) && await VerifyFileAsync(ModelPath, _size, _hash, ct)) return ModelPath;
        var partial = ModelPath + ".partial";
        try
        {
            using var response = await (_client ?? Http).GetAsync(
                _model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
    public static Task<bool> VerifyAsync(string path, SmartModel model, CancellationToken ct) => VerifyFileAsync(path, model.Size, model.Sha256, ct);
    private static async Task<bool> VerifyFileAsync(string path, long size, string hash, CancellationToken ct)
    {
        if (new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
