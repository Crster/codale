namespace Codale.Core.Syntax;

/// <summary>
/// The one shared set of syntax services for the process: the store on disk, the language list,
/// and the tokenizer. The app points it at its data folder once at start-up; anything that asks
/// before that gets the default folder, so tests and tools work without ceremony.
/// </summary>
public static class SyntaxService
{
    private static readonly object Gate = new();
    private static SyntaxStore? _store;
    private static LanguageCatalog? _catalog;
    private static GrammarTokenizer? _tokenizer;

    /// <summary>Points the services at <paramref name="root"/>. Call before first use; later calls replace the services.</summary>
    public static void Initialize(string root)
    {
        lock (Gate)
        {
            _store = new SyntaxStore(root);
            _catalog = new LanguageCatalog(_store, GrammarTokenizer.Shared.Builtin);
            _tokenizer = new GrammarTokenizer(_store, _catalog, GrammarTokenizer.Shared.Builtin);
        }
    }

    public static SyntaxStore Store { get { Ensure(); return _store!; } }

    public static LanguageCatalog Catalog { get { Ensure(); return _catalog!; } }

    public static GrammarTokenizer Tokenizer { get { Ensure(); return _tokenizer!; } }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "syntax");

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_store is null)
            {
                Initialize(DefaultRoot);
            }
        }
    }
}
