using Codale.Core.Shell;

namespace Codale.Core.Tests;

public sealed class ReadableShellCommandTests
{
    private static List<CommandPart> Parts(ReadableCommand command) =>
        [.. command.Lines.SelectMany(l => l.Parts).Where(p => p.Kind != CommandPartKind.Plain)];

    private static CommandPartKind KindOf(ReadableCommand command, string text) =>
        Parts(command).First(p => p.Text == text).Kind;

    [Fact]
    public void Chains_read_one_command_per_line_with_words_for_the_operators()
    {
        var readable = ReadableShellCommand.Parse("cd src && dotnet build -c Release | grep error || echo failed");

        Assert.Equal(
            ["cd src", "then dotnet build -c Release", "piped to grep error", "otherwise echo failed"],
            readable.Lines.Select(l => l.ToString()));
    }

    [Fact]
    public void Program_subcommand_and_flags_are_tagged()
    {
        var readable = ReadableShellCommand.Parse("git -C repo log --oneline -n 5 src/app.cs");

        Assert.Equal(CommandPartKind.Program, KindOf(readable, "git"));
        Assert.Equal(CommandPartKind.Flag, KindOf(readable, "-C"));
        Assert.Equal(CommandPartKind.Flag, KindOf(readable, "--oneline"));
        Assert.Equal(CommandPartKind.Path, KindOf(readable, "src/app.cs"));
    }

    [Fact]
    public void Npm_run_names_the_script_as_a_subcommand()
    {
        var readable = ReadableShellCommand.Parse("npm run build:prod -- --watch");

        Assert.Equal(CommandPartKind.Subcommand, KindOf(readable, "run"));
        Assert.Equal(CommandPartKind.Subcommand, KindOf(readable, "build:prod"));
    }

    [Fact]
    public void Strings_lose_their_escapes_and_spaced_ones_get_typographic_quotes()
    {
        var readable = ReadableShellCommand.Parse("git commit -m \"fix the \\\"quoted\\\" bug\" && grep 'TODO' a.txt");

        Assert.Equal("git commit -m \u201Cfix the \"quoted\" bug\u201D", readable.Lines[0].ToString());
        Assert.Equal("then grep TODO a.txt", readable.Lines[1].ToString());
        Assert.Equal(CommandPartKind.Text, KindOf(readable, "TODO"));
    }

    [Fact]
    public void A_pwsh_wrapper_is_peeled_and_its_quoting_undone()
    {
        var readable = ReadableShellCommand.Parse(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -Command \"Get-ChildItem -Path src -Filter \\\"*.cs\\\" | Select-Object -First 5\"");

        Assert.Equal("PowerShell", readable.Shell);
        Assert.Equal(ShellDialect.PowerShell, readable.Dialect);
        Assert.Equal(["Get-ChildItem -Path src -Filter *.cs", "piped to Select-Object -First 5"], readable.Lines.Select(l => l.ToString()));
        Assert.Equal(CommandPartKind.Program, KindOf(readable, "Get-ChildItem"));
        Assert.Equal(CommandPartKind.Program, KindOf(readable, "Select-Object"));
    }

    [Fact]
    public void An_unquoted_wrapper_path_with_spaces_is_peeled()
    {
        // Codex joins its argv with spaces, so the executable's path arrives unquoted.
        var readable = ReadableShellCommand.Parse(@"C:\Program Files\PowerShell\7\pwsh.exe -Command Get-Content 'notes it''s.txt'");

        Assert.Equal("PowerShell", readable.Shell);
        Assert.Equal("Get-Content \u201Cnotes it's.txt\u201D", readable.ToString());
    }

    [Fact]
    public void A_bash_wrapper_with_single_quotes_is_peeled()
    {
        var readable = ReadableShellCommand.Parse("/bin/bash -lc 'echo '\\''hi there'\\'' > out.txt'");

        Assert.Equal("bash", readable.Shell);
        Assert.Equal("echo \u201Chi there\u201D > out.txt", readable.ToString());
        Assert.Equal(CommandPartKind.Redirect, KindOf(readable, ">"));
        Assert.Equal(CommandPartKind.Path, KindOf(readable, "out.txt"));
    }

    [Fact]
    public void Absolute_program_paths_read_as_the_program_name()
    {
        var readable = ReadableShellCommand.Parse("& \"C:\\Program Files\\Git\\bin\\git.exe\" status");

        Assert.Equal(CommandPartKind.Program, KindOf(readable, "git"));
        Assert.Equal(CommandPartKind.Subcommand, KindOf(readable, "status"));
    }

    [Fact]
    public void Variables_assignments_and_redirects_are_tagged()
    {
        var readable = ReadableShellCommand.Parse("NODE_ENV=production node \"$HOME/app.js\" 2>&1");

        Assert.Equal(CommandPartKind.Variable, KindOf(readable, "NODE_ENV"));
        Assert.Equal(CommandPartKind.Program, KindOf(readable, "node"));
        Assert.Equal(CommandPartKind.Variable, KindOf(readable, "$HOME"));
        Assert.Equal(CommandPartKind.Redirect, KindOf(readable, "2>&1"));
    }

    [Fact]
    public void Pipes_inside_a_script_block_stay_on_the_line()
    {
        var readable = ReadableShellCommand.Parse("Get-ChildItem | ForEach-Object { $_.Name | Out-String }");

        Assert.Equal(2, readable.Lines.Count);
        Assert.Equal("piped to ForEach-Object { $_.Name | Out-String }", readable.Lines[1].ToString());
        Assert.Equal(CommandPartKind.Program, KindOf(readable, "Out-String"));
    }

    [Fact]
    public void Heredoc_body_reads_as_text_lines()
    {
        var readable = ReadableShellCommand.Parse("cat <<'EOF' > notes.md\nline one\nline two\nEOF\necho done");

        Assert.Equal(["cat <<EOF > notes.md", "    line one", "    line two", "echo done"], readable.Lines.Select(l => l.ToString()));
    }

    [Fact]
    public void Windows_paths_keep_their_backslashes()
    {
        var readable = ReadableShellCommand.Parse(@"type C:\Users\dev\notes.txt");

        Assert.Equal(@"type C:\Users\dev\notes.txt", readable.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"unterminated")]
    [InlineData("echo $(")]
    [InlineData("pwsh -Command")]
    [InlineData("a | | && ;; > < }{ ) (")]
    [InlineData("cat <<EOF")]
    public void Odd_input_never_throws(string command)
    {
        _ = ReadableShellCommand.Parse(command);
    }
}
