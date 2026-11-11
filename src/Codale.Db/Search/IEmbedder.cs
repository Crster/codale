using Codale.Core.Features;

namespace Codale.Db;

/// <summary>Turns text into a unit-length vector of a fixed dimension. Swap in a trained model for true semantic matching.</summary>
public interface IEmbedder
{
    /// <summary>Stored beside every vector; changing it makes the database re-embed lazily.</summary>
    string Model { get; }

    float[] Embed(string text);
}

/// <summary>The default: Codale's local hashed n-gram embedder. Lexical (word, stem and sub-word overlap), not synonym-aware.</summary>
public sealed class HashedEmbedder : IEmbedder
{
    public string Model => HashedTextEmbedder.Instance.Model;

    public float[] Embed(string text) => HashedTextEmbedder.Instance.Embed(text);
}
