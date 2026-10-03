using System.Text;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class ShellOutputCompressorTests
{
    private static string Repeat(int count, Func<int, string> line) =>
        string.Join('\n', Enumerable.Range(1, count).Select(line));

    [Fact]
    public void Small_output_is_returned_untouched()
    {
        const string output = "\u001b[32mok\u001b[0m\nok\nok\n";
        var result = ShellOutputCompressor.Compress("git status", output, "");

        Assert.False(result.Changed);
        Assert.Same(output, result.Stdout);
    }

    [Fact]
    public void Dotnet_build_keeps_each_diagnostic_once_and_the_summary()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            log.AppendLine($"  Restored X:\\repo\\src\\Project{i}\\Project{i}.csproj (in 120 ms).");
        }

        const string error = @"X:\repo\src\App\Foo.cs(12,5): error CS0103: The name 'bar' does not exist in the current context [X:\repo\src\App\App.csproj]";
        log.AppendLine(error);
        for (var i = 0; i < 40; i++)
        {
            log.AppendLine($"  Project{i} -> X:\\repo\\src\\Project{i}\\bin\\Debug\\net10.0\\Project{i}.dll");
        }

        log.AppendLine("Build FAILED.");
        log.AppendLine(error);
        log.AppendLine("    0 Warning(s)");
        log.AppendLine("    1 Error(s)");

        var result = ShellOutputCompressor.Compress("dotnet build src/App", log.ToString(), "");

        Assert.True(result.Changed);
        Assert.Single(result.Stdout.Split('\n'), l => l.Contains("CS0103"));
        Assert.Contains(@"Foo.cs(12,5): error CS0103: The name 'bar' does not exist in the current context", result.Stdout);
        Assert.Contains("Build FAILED.", result.Stdout);
        Assert.Contains("1 Error(s)", result.Stdout);
        Assert.DoesNotContain("Restored", result.Stdout);
        Assert.Contains("lines of build progress omitted", result.Stdout);
        Assert.True(result.Stdout.Length < log.Length / 5);
    }

    [Fact]
    public void Noisy_warning_codes_are_capped_with_a_count()
    {
        var log = Repeat(30, i => $@"X:\repo\A{i}.cs(1,1): warning CS8618: Non-nullable property 'P{i}' must contain a non-null value [X:\repo\A.csproj]") +
                  "\nBuild succeeded.\n" + new string(' ', 10);

        var result = ShellOutputCompressor.Compress("dotnet build", log, "");

        Assert.Equal(5, result.Stdout.Split('\n').Count(l => l.Contains("warning CS8618")));
        Assert.Contains("(+25 more CS8618 warnings)", result.Stdout);
    }

    [Fact]
    public void Test_runs_drop_passing_tests_and_keep_failures_verbatim()
    {
        var log = Repeat(200, i => $"  Passed Codale.Tests.Suite.Case{i} [3 ms]") +
                  "\n  Failed Codale.Tests.Suite.Broken [12 ms]\n  Error Message:\n   Assert.Equal() Failure: Values differ\n" +
                  "  Stack Trace:\n     at Codale.Tests.Suite.Broken() in X:\\repo\\tests\\Suite.cs:line 42\n" +
                  "\nFailed!  - Failed:     1, Passed:   200, Skipped:     0, Total:   201";

        var result = ShellOutputCompressor.Compress("dotnet test", log, "");

        Assert.DoesNotContain("Case17", result.Stdout);
        Assert.Contains("[200 passing test lines omitted]", result.Stdout);
        Assert.Contains("Failed Codale.Tests.Suite.Broken [12 ms]", result.Stdout);
        Assert.Contains("Assert.Equal() Failure: Values differ", result.Stdout);
        Assert.Contains("Suite.cs:line 42", result.Stdout);
        Assert.Contains("Failed:     1, Passed:   200", result.Stdout);
    }

    [Fact]
    public void Ansi_codes_and_progress_redraws_are_stripped()
    {
        var frames = string.Concat(Enumerable.Range(0, 300).Select(i => $"\rDownloading {i}%"));
        var output = $"\u001b[1mstart\u001b[0m\n{frames}\rDownloading done\nfinished\n" + Repeat(40, i => $"line {i} of the real output that matters a little");

        var result = ShellOutputCompressor.CompressStream(CommandKind.Other, output);

        Assert.False(result.Contains('\u001b'));
        Assert.DoesNotContain("Downloading 17%", result);
        Assert.Contains("Downloading done", result);
        Assert.Contains("start", result);
    }

    [Fact]
    public void Repeated_lines_collapse_to_a_count_but_errors_survive()
    {
        var output = Repeat(50, _ => "error: the same failure, again and again") + "\n" +
                     Repeat(50, _ => "plain repeated noise line here");

        var result = ShellOutputCompressor.CompressStream(CommandKind.Other, output);

        Assert.Contains("error: the same failure, again and again (x50)", result);
        Assert.Contains("plain repeated noise line here (x50)", result);
    }

    [Fact]
    public void Lines_that_differ_only_in_numbers_collapse_to_their_ends()
    {
        var output = Repeat(100, i => $"[12:00:{i:00}] processed item {i} of 100");

        var result = ShellOutputCompressor.CompressStream(CommandKind.Other, output);

        Assert.Contains("processed item 1 of 100", result);
        Assert.Contains("processed item 100 of 100", result);
        Assert.Contains("[... 97 more like these ...]", result);
    }

    [Fact]
    public void Npm_install_drops_deprecations_and_keeps_errors()
    {
        var output = Repeat(80, i => $"npm WARN deprecated package{i}@1.0.0: this library is no longer supported") +
                     "\nnpm ERR! code ERESOLVE\nnpm ERR! Could not resolve dependency\nadded 812 packages in 14s";

        var result = ShellOutputCompressor.Compress("npm install", output, "");

        Assert.DoesNotContain("package17@", result.Stdout);
        Assert.Contains("(80 deprecation warnings omitted)", result.Stdout);
        Assert.Contains("npm ERR! code ERESOLVE", result.Stdout);
        Assert.Contains("added 812 packages", result.Stdout);
    }

    [Fact]
    public void Head_tail_keeps_the_ends_and_the_problem_lines_between()
    {
        var output = Repeat(1000, i => i == 500 ? "fatal: something broke at line 500" : $"line {i}");

        var cut = ShellOutputCompressor.HeadTail(output, head: 10, tail: 10);

        Assert.Contains("line 1\n", cut);
        Assert.Contains("line 1000", cut);
        Assert.Contains("fatal: something broke at line 500", cut);
        Assert.DoesNotContain("line 400\n", cut);
    }

    [Theory]
    [InlineData("dotnet test tests/Foo", CommandKind.Test)]
    [InlineData("cd src && npx vitest run", CommandKind.Test)]
    [InlineData("dotnet build -c Release", CommandKind.DotnetBuild)]
    [InlineData("pnpm install", CommandKind.PackageInstall)]
    [InlineData("git status --short", CommandKind.GitStatus)]
    [InlineData("git diff", CommandKind.Other)]
    public void Commands_are_classified(string command, CommandKind kind) =>
        Assert.Equal(kind, ShellOutputCompressor.Classify(command));
}
