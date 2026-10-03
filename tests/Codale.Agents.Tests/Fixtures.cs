namespace Codale.Agents.Tests;

internal static class Fixtures
{
    /// <summary>
    /// Fixtures are shared by several test projects, so they live at the repo's
    /// tests/fixtures rather than being copied into each project's output.
    /// </summary>
    public static string Path(string name) => Find(System.IO.Path.Combine("claude", name));

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "fixtures", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Fixture '{relative}' not found walking up from {AppContext.BaseDirectory}.");
    }
}
