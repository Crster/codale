using Codale.Core.Projects;

namespace Codale.Core.Tests;

public sealed class ProjectPathsTests
{
    /// <summary>
    /// The mapping was derived by running claude 2.1.274 in known directories and
    /// observing which folder appeared under ~/.claude/projects. The rule is:
    /// every character outside [A-Za-z0-9-] becomes '-', and case is preserved.
    /// </summary>
    [Theory]
    [InlineData(@"X:\CrsterSite\Codale", "X--CrsterSite-Codale")]
    [InlineData(@"C:\Users\Crster\dot.test\work", "C--Users-Crster-dot-test-work")]
    [InlineData(@"C:\a\my_app (v2)\work", "C--a-my-app--v2--work")]
    public void Claude_history_folder_name_matches_the_observed_escaping(string path, string expected)
    {
        Assert.Equal(expected, ProjectPaths.ClaudeHistoryFolderName(path));
    }

    [Fact]
    public void History_folder_preserves_dashes_so_guids_survive_intact()
    {
        var name = ProjectPaths.ClaudeHistoryFolderName(@"C:\t\f5851afc-7646-4547-b753-7b3bd5e23594\x");
        Assert.Contains("f5851afc-7646-4547-b753-7b3bd5e23594", name);
    }

    [Theory]
    [InlineData(@"X:\CrsterSite\Codale\", @"X:\CrsterSite\Codale")]
    [InlineData(@"X:\CrsterSite\Codale", @"X:\CrsterSite\Codale")]
    [InlineData(@"X:\CrsterSite\..\CrsterSite\Codale", @"X:\CrsterSite\Codale")]
    [InlineData("\"X:\\CrsterSite\\Codale\"", @"X:\CrsterSite\Codale")]
    public void Normalize_collapses_equivalent_spellings(string input, string expected)
    {
        Assert.Equal(expected, ProjectPaths.Normalize(input));
    }

    [Fact]
    public void Normalize_keeps_the_root_of_a_drive_usable()
    {
        Assert.Equal(@"X:\", ProjectPaths.Normalize(@"X:\"));
    }

    [Fact]
    public void Instance_key_is_stable_and_case_insensitive()
    {
        // Windows paths are case insensitive, so these must share one window.
        var a = ProjectPaths.InstanceKey(@"X:\CrsterSite\Codale");
        var b = ProjectPaths.InstanceKey(@"x:\crstersite\codale\");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Different_projects_get_different_instance_keys()
    {
        Assert.NotEqual(
            ProjectPaths.InstanceKey(@"X:\CrsterSite\Codale"),
            ProjectPaths.InstanceKey(@"X:\CrsterSite\Evania"));
    }

    [Fact]
    public void Display_name_is_the_leaf_folder()
    {
        Assert.Equal("Codale", ProjectPaths.DisplayName(@"X:\CrsterSite\Codale\"));
    }

    [Fact]
    public void IsInside_accepts_the_root_itself_and_anything_beneath_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        Assert.True(ProjectPaths.IsInside(root, root));
        Assert.True(ProjectPaths.IsInside(root, root + Path.DirectorySeparatorChar));
        Assert.True(ProjectPaths.IsInside(root, Path.Combine(root, "src", "a.cs")));
        Assert.True(ProjectPaths.IsInside(root + Path.DirectorySeparatorChar, Path.Combine(root, "a.cs")));
    }

    [Fact]
    public void IsInside_refuses_a_sibling_that_merely_shares_the_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        Assert.False(ProjectPaths.IsInside(root, root + "-secrets"));
        Assert.False(ProjectPaths.IsInside(root, Path.Combine(root + "-secrets", "key.txt")));
        Assert.False(ProjectPaths.IsInside(root, Path.GetTempPath()));
    }

    [Fact]
    public void IsInside_does_not_let_dot_dot_segments_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        Assert.False(ProjectPaths.IsInside(root, Path.Combine(root, "..", "other", "a.cs")));
        Assert.True(ProjectPaths.IsInside(root, Path.Combine(root, "src", "..", "a.cs")));
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", " ")]
    public void IsInside_is_false_for_blank_input(string root, string path) =>
        Assert.False(ProjectPaths.IsInside(root, path));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_rejects_empty_input(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProjectPaths.Normalize(input));
    }
}
