namespace Codale.Smart;

public enum SmartProtocol { Lfm3, Gemma4 }

public sealed record SmartModel(string Id, string Name, string Repository, string Revision,
    string FileName, long Size, string Sha256, SmartProtocol Protocol, bool Released)
{
    public string DownloadUrl => $"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}";
    public string DisplayName => $"{Name} · {Size / 1_000_000_000d:0.00} GB";
}

public static class SmartModels
{
    public static SmartModel Default { get; } = new("lfm3-1.7b-q4", "LFM3 1.7B Q4_K_M",
        "LiquidAI/LFM3-1.7B-GGUF", ModelInstaller.Revision, ModelInstaller.FileName,
        ModelInstaller.Size, ModelInstaller.Sha256, SmartProtocol.Lfm3, true);

    // Release only after native compatibility AND the 8 GB CPU resource check pass.
    public static SmartModel Gemma4 { get; } = new("gemma4-e2b-q4", "Gemma 4 E2B Q4_0",
        "google/gemma-4-E2B-it-qat-q4_0-gguf", "675cff42a74c774d6cb76f76d8eacb49b48c9b93",
        "gemma-4-E2B_q4_0-it.gguf", 3349516256,
        "fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634", SmartProtocol.Gemma4, false);

    public static IReadOnlyList<SmartModel> All { get; } = Array.AsReadOnly(new[] { Default, Gemma4 });
    public static SmartModel Resolve(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Default;
}
