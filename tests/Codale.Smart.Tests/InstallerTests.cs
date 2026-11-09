using System.Net;
using System.Security.Cryptography;
using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-model-tests-").FullName;
    [Fact]
    public async Task Verified_download_is_reused_offline()
    {
        byte[] data = [1, 2, 3, 4];
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        using var http = new HttpClient(handler);
        var installer = new ModelInstaller(_root, http, data.Length, Convert.ToHexString(SHA256.HashData(data)));
        var path = await installer.EnsureAsync(null, default);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".partial"));
        handler.Reply = () => throw new HttpRequestException("offline");
        Assert.Equal(path, await installer.EnsureAsync(null, default));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Corrupt_download_never_becomes_the_installed_model_and_can_be_retried()
    {
        byte[] data = [1, 2, 3, 4];
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9, 9]) });
        using var http = new HttpClient(handler);
        var installer = new ModelInstaller(_root, http, data.Length, Convert.ToHexString(SHA256.HashData(data)));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.EnsureAsync(null, default));
        Assert.False(File.Exists(installer.ModelPath));
        Assert.False(File.Exists(installer.ModelPath + ".partial"));
        handler.Reply = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        Assert.Equal(installer.ModelPath, await installer.EnsureAsync(null, default));
    }
    [Fact]
    public async Task Cancellation_during_download_cleans_up_and_releases_download_lock()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(cancel)) });
        using var http = new HttpClient(handler);
        var installer = new ModelInstaller(_root, http, 4, "unused");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.EnsureAsync(null, cancel.Token));
        Assert.False(File.Exists(installer.ModelPath + ".partial"));
        using var lease = new FileStream(installer.ModelPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private sealed class Handler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpResponseMessage> Reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(Reply()); }
    }
    private sealed class CancelStream(CancellationTokenSource cancel) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { cancel.Cancel(); return ValueTask.FromCanceled<int>(ct); }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
