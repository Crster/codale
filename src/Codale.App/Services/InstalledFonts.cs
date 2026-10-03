using Microsoft.Win32;

namespace Codale.App.Services;

/// <summary>
/// The installed font families, read once from the font registrations (machine-wide and
/// per-user). Win2D 1.3 exposes no DirectWrite font-enumeration API to probe with, and
/// DirectWrite silently substitutes a fallback family for an unknown name, so settings
/// that take a font family check it here first.
/// </summary>
public static class InstalledFonts
{
    /// <summary>Family names from the registrations; empty only if the registry read failed.</summary>
    private static readonly HashSet<string> Families = Collect();

    /// <summary>
    /// Returns <paramref name="requested"/> when it is installed (or when the check
    /// failed and there is nothing better to go on), else <paramref name="fallback"/>.
    /// </summary>
    public static string Resolve(string requested, string fallback) =>
        Families.Count == 0 || Families.Contains(requested) ? requested : fallback;

    private static HashSet<string> Collect()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(Registry.LocalMachine, names);
        Collect(Registry.CurrentUser, names);
        return names;
    }

    private static void Collect(RegistryKey root, HashSet<string> names)
    {
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
        if (key is null)
        {
            return;
        }

        // Value names read like "Cascadia Mono (TrueType)"; the trailing file-format
        // hint is not part of the family name.
        foreach (var value in key.GetValueNames())
        {
            var family = value;
            var open = family.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && family.EndsWith(')'))
            {
                family = family[..open];
            }

            names.Add(family);
        }
    }
}
