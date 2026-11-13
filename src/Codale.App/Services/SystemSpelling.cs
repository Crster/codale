using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Codale.App.Services;

/// <summary>
/// The Windows spell checker (the one RichEdit draws its squiggles from), asked directly so the
/// composer can offer a correction without marking anything. Created on first use, off the UI
/// thread; every call is serialized. Unavailable (no language pack, COM failure) means it answers
/// "correct" and suggests nothing.
/// </summary>
internal static class SystemSpelling
{
    private static readonly Lock Gate = new();
    private static ISpellChecker? _checker;
    private static bool _tried;

    /// <summary>True when the checker knows the word, or there is no checker to ask.</summary>
    public static bool IsCorrect(string word)
    {
        lock (Gate)
        {
            if (Checker() is not { } checker)
            {
                return true;
            }

            try
            {
                checker.Check(word, out var errors);
                try
                {
                    return errors.Next(out var error) != 0 || error is null;
                }
                finally
                {
                    Marshal.ReleaseComObject(errors);
                }
            }
            catch (COMException)
            {
                return true;
            }
        }
    }

    /// <summary>The checker's replacements for a misspelled word, best first.</summary>
    public static IReadOnlyList<string> Suggest(string word, int max = 3)
    {
        lock (Gate)
        {
            if (Checker() is not { } checker)
            {
                return [];
            }

            var result = new List<string>();
            try
            {
                checker.Suggest(word, out var list);
                try
                {
                    var one = new string[1];
                    while (result.Count < max && list.Next(1, one, IntPtr.Zero) == 0)
                    {
                        result.Add(one[0]);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(list);
                }
            }
            catch (COMException)
            {
            }

            return result;
        }
    }

    private static ISpellChecker? Checker()
    {
        if (_tried)
        {
            return _checker;
        }

        _tried = true;
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            foreach (var tag in new[] { CultureInfo.CurrentUICulture.Name, "en-US" })
            {
                if (tag.Length > 0 && factory.IsSupported(tag, out var supported) == 0 && supported != 0)
                {
                    factory.CreateSpellChecker(tag, out _checker);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _checker = null;
        }

        return _checker;
    }

    [ComImport]
    [Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass;

    [ComImport]
    [Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void GetSupportedLanguages(out IEnumString languages);

        [PreserveSig]
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int supported);

        void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker checker);
    }

    [ComImport]
    [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        void GetLanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string tag);

        void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError errors);

        void Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString suggestions);
    }

    [ComImport]
    [Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        /// <summary>S_OK (0) with an error, S_FALSE (1) when there are no more.</summary>
        [PreserveSig]
        int Next(out ISpellingError? error);
    }

    [ComImport]
    [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError;
}
