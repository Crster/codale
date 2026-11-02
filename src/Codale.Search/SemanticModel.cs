using Codale.Core.Features;

namespace Codale.Search;

/// <summary>
/// The semantic embedding model for project retrieval and topic detection: minishlab/potion-base-8M
/// (Model2Vec, MIT). It ships beside the app exe under <c>models\potion-base-8M</c>, cached by run.ps1
/// into tools\models from a pinned revision checked by SHA-256, the way ripgrep ships. It is loaded once
/// per process in the background (~10 MB in memory once quantized); until then, or if the files are
/// missing, callers fall back to the hashed embedder. Nothing is fetched at run time.
/// </summary>
public static class SemanticModel
{
    public const string ModelId = "potion-base-8M-q8";

    private static readonly object Gate = new();
    private static Task<ITextEmbedder?>? _loading;
    private static ITextEmbedder? _current;

    /// <summary>The loaded model, or null while it is still loading or when it is not bundled.</summary>
    public static ITextEmbedder? Current => Volatile.Read(ref _current);

    /// <summary>The best embedder available right now: the semantic model when loaded, else the hashed one.</summary>
    public static ITextEmbedder Best => Current ?? HashedTextEmbedder.Instance;

    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "models", "potion-base-8M");

    /// <summary>Starts loading the bundled model in the background, once per process; later calls return the same task.</summary>
    public static Task<ITextEmbedder?> EnsureLoadedAsync()
    {
        lock (Gate)
        {
            return _loading ??= Task.Run(() =>
            {
                try
                {
                    var model = Load(BundledDirectory);
                    Volatile.Write(ref _current, model);
                    return (ITextEmbedder?)model;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    System.Diagnostics.Trace.TraceWarning($"Semantic model unavailable, using hashed embeddings: {ex.Message}");
                    return null;
                }
            });
        }
    }

    /// <summary>Loads the model from <paramref name="directory"/> (<c>vocab.txt</c> and <c>model.safetensors</c>).</summary>
    public static StaticEmbedder Load(string directory) =>
        StaticEmbedder.Load(ModelId, Path.Combine(directory, "vocab.txt"), Path.Combine(directory, "model.safetensors"));
}
