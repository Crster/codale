using System.Text;
using System.Text.Json;

namespace Codale.Terminal;

/// <summary>
/// The folder and environment a command block starts in. Every block runs in a process of its own,
/// so what one command changes (a <c>cd</c>, an <c>$env:</c> assignment) only reaches the next
/// block because the finished block writes its final state to a file that is read back into this.
/// </summary>
public sealed record ShellState(string Cwd, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>A first block's state: <paramref name="cwd"/> and this process's own environment.</summary>
    public static ShellState Initial(string cwd)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        return new ShellState(cwd, environment);
    }

    /// <summary>The state a block left behind in <paramref name="stateFile"/>; null when it wrote none (it called exit, or was stopped).</summary>
    public static ShellState? Read(string stateFile)
    {
        try
        {
            return File.Exists(stateFile) ? Parse(File.ReadAllText(stateFile)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a state file: PowerShell writes JSON (<c>{"cwd":..,"env":{..}}</c>); cmd writes the
    /// folder on the first line and <c>set</c>'s NAME=value lines after it.
    /// </summary>
    public static ShellState? Parse(string text)
    {
        text = text.TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("cwd", out var cwd) || cwd.GetString() is not { Length: > 0 } folder)
                {
                    return null;
                }

                if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                {
                    foreach (var variable in env.EnumerateObject())
                    {
                        if (variable.Value.ValueKind == JsonValueKind.String)
                        {
                            environment[variable.Name] = variable.Value.GetString()!;
                        }
                    }
                }

                return new ShellState(folder, environment);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var first = lines[0].Trim();
        if (first.Length == 0)
        {
            return null;
        }

        foreach (var line in lines.Skip(1))
        {
            // cmd's hidden per-drive variables ("=C:=C:\x") start with '='; skip them and the wrapper's own.
            var split = line.IndexOf('=', 1);
            if (line.Length == 0 || line[0] == '=' || split <= 0)
            {
                continue;
            }

            var name = line[..split];
            if (!name.Equals(BlockCommand.CmdExitVariable, StringComparison.OrdinalIgnoreCase))
            {
                environment[name] = line[(split + 1)..];
            }
        }

        return new ShellState(first, environment);
    }
}

/// <summary>What starting one command block takes: the process command line and where its state lands.</summary>
/// <param name="CommandLine">The line handed to CreateProcess.</param>
/// <param name="StateFile">Where the block writes its final folder and environment.</param>
/// <param name="ScriptFile">A temporary script the command line runs (cmd only); deleted with the state file.</param>
public sealed record BlockLaunch(string CommandLine, string StateFile, string? ScriptFile)
{
    /// <summary>Deletes the block's temporary files; safe to call more than once.</summary>
    public void Cleanup()
    {
        foreach (var path in new[] { StateFile, ScriptFile })
        {
            try
            {
                if (path is not null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Builds the command line for one command block: the user's command, run by the chosen shell in a
/// process of its own, followed by a postlude that saves the folder and environment it ended with
/// (see <see cref="ShellState"/>) and exits with the command's own exit code.
/// </summary>
public static class BlockCommand
{
    /// <summary>The variable the cmd wrapper keeps the exit code in; left out of the saved environment.</summary>
    public const string CmdExitVariable = "__codale_exit";

    public static bool IsPowerShell(string shell)
    {
        var name = Path.GetFileName(shell);
        return name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder the per-block state and script files go in.</summary>
    public static string DefaultStateDirectory => Path.Combine(Path.GetTempPath(), "codale-blocks");

    /// <summary>
    /// Builds the launch for <paramref name="command"/> under <paramref name="shell"/>. Files go in
    /// <paramref name="stateDirectory"/> (created if missing). <paramref name="loadProfile"/> runs the
    /// user's PowerShell profile first - off by default because a block pays for it every time.
    /// </summary>
    public static BlockLaunch Build(string shell, string command, string stateDirectory, bool loadProfile = false)
    {
        Directory.CreateDirectory(stateDirectory);
        var id = Guid.NewGuid().ToString("N");
        var stateFile = Path.Combine(stateDirectory, id + ".state");

        if (IsPowerShell(shell))
        {
            var script = PowerShellScript(command, stateFile);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var profile = loadProfile ? "" : "-NoProfile ";
            return new BlockLaunch($"\"{shell}\" -NoLogo {profile}-EncodedCommand {encoded}", stateFile, null);
        }

        var scriptFile = Path.Combine(stateDirectory, id + ".cmd");
        File.WriteAllText(scriptFile, CmdScript(command, stateFile), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new BlockLaunch($"\"{shell}\" /d /c \"\"{scriptFile}\"\"", stateFile, scriptFile);
    }

    /// <summary>
    /// The PowerShell a block runs. The command is parsed as a script block and dot-sourced, so a
    /// syntax error is reported (and the state still saved) instead of the whole wrapper failing to
    /// parse, and what it changes stays in the wrapper's scope for the postlude to read.
    /// </summary>
    public static string PowerShellScript(string command, string stateFile)
    {
        var quotedCommand = Quote(command);
        var quotedState = Quote(stateFile);
        return $$"""
            $global:LASTEXITCODE = $null
            $__codaleOk = $true
            $__codaleErrors = $global:Error.Count
            try {
                # Out-Default here, not at the end of the script: objects are formatted in batches,
                # and the exit below would otherwise drop a table still waiting to be printed.
                . ([scriptblock]::Create({{quotedCommand}})) | Out-Default
                $__codaleOk = $? -and $global:Error.Count -eq $__codaleErrors
            } catch {
                $__codaleOk = $false
                Write-Error -ErrorRecord $_ -ErrorAction Continue
            }
            $__codaleCode = if ($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0) { $global:LASTEXITCODE } elseif ($__codaleOk) { 0 } else { 1 }
            try {
                $__codaleCwd = (Get-Location -PSProvider FileSystem).ProviderPath
                $__codaleState = @{ cwd = $__codaleCwd; env = [Environment]::GetEnvironmentVariables(); exit = $__codaleCode }
                [IO.File]::WriteAllText({{quotedState}}, ($__codaleState | ConvertTo-Json -Compress -Depth 3))
            } catch { }
            exit $__codaleCode
            """;

        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    }

    /// <summary>The batch file a cmd block runs: the command, then its folder and environment saved.</summary>
    public static string CmdScript(string command, string stateFile) =>
        $"""
        @echo off
        chcp 65001 >nul
        {command}
        set "{CmdExitVariable}=%errorlevel%"
        (cd & set) > "{stateFile}"
        exit /b %{CmdExitVariable}%
        """;
}
