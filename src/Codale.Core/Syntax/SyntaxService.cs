namespace Codale.Core.Syntax;

/// <summary>
/// The one shared set of syntax services for the process: the store on disk, the language list,
/// and the tokenizer. The app points it at its data folder once at start-up; anything that asks
/// before that gets the default folder, so tests and tools work without ceremony.
/// </summary>
public static class SyntaxService
{
    private static readonly Lock Gate = new();
    private static SyntaxStore? s_store;
    private static LanguageCatalog? s_catalog;

    /// <summary>Points the services at <paramref name="root"/>. Call before first use; later calls replace the services.</summary>
    public static void Initialize(string root)
    {
        lock (Gate)
        {
            s_store = new SyntaxStore(root);
            s_catalog = new LanguageCatalog(s_store, GrammarTokenizer.Shared.Builtin);
            Tokenizer = new GrammarTokenizer(s_store, s_catalog, GrammarTokenizer.Shared.Builtin);
        }
    }

    public static SyntaxStore Store { get { Ensure(); return s_store!; } }

    public static LanguageCatalog Catalog { get { Ensure(); return s_catalog!; } }

    public static GrammarTokenizer Tokenizer { get { Ensure(); return field!; }

        private set;
    }

    public static string DefaultRoot =>
        Path.Combine(Codale.Core.Projects.CodalePaths.DataRoot, "syntax");

    private static void Ensure()
    {
        lock (Gate)
        {
            if (s_store is null)
            {
                Initialize(DefaultRoot);
            }
        }
    }
}