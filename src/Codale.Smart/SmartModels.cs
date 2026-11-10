namespace Codale.Smart;

public sealed record SmartModel(string Id, string Name, string Repository, string Revision,
    string FileName, long Size, string Sha256)
{
    public string DownloadUrl => $"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}";
    public string DisplayName => $"{Name} · {Size / 1_000_000_000d:0.00} GB";
}

public static class SmartModels
{
    public static SmartModel Default { get; } = new("lfm3-1.7b-q4", "LFM3 1.7B Q4_K_M",
        "LiquidAI/LFM3-1.7B-GGUF", ModelInstaller.Revision, ModelInstaller.FileName,
        ModelInstaller.Size, ModelInstaller.Sha256);

    public static IReadOnlyList<SmartModel> All { get; } = Array.AsReadOnly(new[] { Default });
    public static SmartModel Resolve(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Default;
}
