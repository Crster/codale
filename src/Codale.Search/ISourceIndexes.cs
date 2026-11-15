
using Codale.Search.Engine;

namespace Codale.Search;

/// <summary>Supplies the source index of a project root. The application uses the shared per-root cache.</summary>
public interface ISourceIndexes
{
    SourceIndex For(string root);
}

/// <summary>The process-wide per-root cache that the title bar, the explore tool and the editor share.</summary>
public sealed class SharedSourceIndexes : ISourceIndexes
{
    public static readonly SharedSourceIndexes Instance = new();

    public SourceIndex For(string root) => SourceIndex.For(root);
}