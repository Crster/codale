using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Codale.Terminal;

/// <summary>
/// Where the notebook input's ghost suggestion comes from when its shell is PowerShell: the
/// PSReadLine history every PowerShell console writes (what the user ran there, newest first), then
/// PowerShell's own tab completion (<c>TabExpansion2</c>: commands, parameters, paths, and any
/// argument completers the session has loaded).
/// </summary>
public static class PowerShellSuggestions
{
    private const int MaxHistory = 10_000;

    private static readonly Lock HistoryGate = new();
    private static IReadOnlyList<string> s_history = [];
    private static DateTime s_historyStamp;
    private static Task? s_historyLoad;

    /// <summary>Whether <paramref name="shellPath"/> is pwsh or Windows PowerShell.</summary>
    public static bool IsPowerShell(string shellPath)
    {
        var name = Path.GetFileName(shellPath);
        return name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PSReadLine's history file for the console host, shared by pwsh and Windows PowerShell.</summary>
    public static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");

    /// <summary>
    /// The newest one-line command from the PSReadLine history that starts with
    /// <paramref name="typed"/> (case-insensitively) and goes further. The file is read in the
    /// background the first time and again whenever it changes; until then this has nothing.
    /// </summary>
    public static string? FromHistory(string typed)
    {
        if (typed.Trim().Length == 0)
        {
            return null;
        }

        RefreshHistory();
        IReadOnlyList<string> history;
        lock (HistoryGate)
        {
            history = s_history;
        }

        foreach (var command in history)
        {
            if (command.Length > typed.Length && command.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !command.Contains('\n'))
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>
    /// The lines of a PSReadLine history file as commands, newest first and each once. PSReadLine
    /// writes a multi-line command with a backtick ending every line but its last.
    /// </summary>
    public static IReadOnlyList<string> ParseHistory(IEnumerable<string> lines, int max = MaxHistory)
    {
        var commands = new List<string>();
        var pending = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.EndsWith('`'))
            {
                pending.Append(line, 0, line.Length - 1).Append('\n');
                continue;
            }

            pending.Append(line);
            var command = pending.ToString().Trim();
            pending.Clear();
            if (command.Length > 0)
            {
                commands.Add(command);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = new List<string>();
        for (var i = commands.Count - 1; i >= 0 && newestFirst.Count < max; i--)
        {
            if (seen.Add(commands[i]))
            {
                newestFirst.Add(commands[i]);
            }
        }

        return newestFirst;
    }

    /// <summary>
    /// What one completion makes of the whole line: the text with the word being completed replaced
    /// by the first match that carries it further, or null when none does. Only a match that keeps
    /// the typed word as its start can be shown as a ghost after the caret; a leading <c>.\</c>
    /// PowerShell adds to a relative path is dropped so <c>cd sr</c> still becomes <c>cd src</c>.
    /// </summary>
    public static string? CompleteLine(string typed, int replacementIndex, int replacementLength, IReadOnlyList<string> matches)
    {
        if (replacementIndex < 0 || replacementLength < 0 || replacementIndex + replacementLength != typed.Length)
        {
            return null;
        }

        var word = typed.Substring(replacementIndex, replacementLength);
        if (word.Trim().Length < 2)
        {
            // A bare "-" or the start of a new word would just show the first of everything.
            return null;
        }

        foreach (var match in matches)
        {
            var candidate = match;
            if (!word.StartsWith(@".\", StringComparison.Ordinal) && candidate.StartsWith(@".\", StringComparison.Ordinal))
            {
                candidate = candidate[2..];
            }

            if (candidate.Length > word.Length && candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                return typed[..replacementIndex] + word + candidate[word.Length..];
            }
        }

        return null;
    }

    private static void RefreshHistory()
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(HistoryPath);
        }
        catch (Exception)
        {
            return;
        }

        lock (HistoryGate)
        {
            if (stamp == s_historyStamp || s_historyLoad is { IsCompleted: false })
            {
                return;
            }

            s_historyStamp = stamp;
            s_historyLoad = Task.Run(LoadHistory);
        }
    }

    private static void LoadHistory()
    {
        try
        {
            using var stream = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            var history = ParseHistory(lines);
            lock (HistoryGate)
            {
                s_history = history;
            }
        }
        catch (Exception)
        {
            // No history (never used PowerShell interactively, or the file is locked): nothing to offer.
        }
    }
}

/// <summary>
/// A hidden PowerShell process kept warm to answer tab completion for the notebook input. One per
/// shell (and profile choice), started on first use; requests are answered one at a time, and a
/// request whose caller moved on while it waited is dropped before it reaches PowerShell.
/// </summary>
public sealed class PowerShellCompletionHost
{
    private const int MaxStarts = 3;

    /// <summary>Reads a request per line, answers each with one "@@"-marked JSON line; anything else on stdout is ignored.</summary>
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $reader = New-Object System.IO.StreamReader ([Console]::OpenStandardInput()), $utf8
        $writer = [Console]::OpenStandardOutput()
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $id = 0; $index = 0; $length = 0; $found = @()
            try {
                $request = ConvertFrom-Json $line
                $id = $request.id
                if ($request.cwd) { Set-Location -LiteralPath $request.cwd }
                $result = TabExpansion2 -inputScript $request.text -cursorColumn $request.text.Length
                if ($result) {
                    $index = $result.ReplacementIndex
                    $length = $result.ReplacementLength
                    $found = @($result.CompletionMatches | Select-Object -First 30 | ForEach-Object { $_.CompletionText })
                }
            } catch { }
            $json = ConvertTo-Json -Compress -InputObject @{ id = $id; index = $index; length = $length; matches = $found }
            $bytes = $utf8.GetBytes('@@' + $json + "`n")
            $writer.Write($bytes, 0, $bytes.Length)
            $writer.Flush()
        }
        """;

    private static readonly ConcurrentDictionary<string, PowerShellCompletionHost> Hosts = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _shell;
    private readonly bool _loadProfile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _waiting = new();
    private Process? _process;
    private long _nextId;
    private int _starts;
    private bool _warm;

    static PowerShellCompletionHost()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var host in Hosts.Values)
            {
                host.Stop();
            }
        };
    }

    private PowerShellCompletionHost(string shell, bool loadProfile)
    {
        _shell = shell;
        _loadProfile = loadProfile;
    }

    /// <summary>The host for <paramref name="shell"/>; with <paramref name="loadProfile"/> it runs the user's profile, so its argument completers count.</summary>
    public static PowerShellCompletionHost For(string shell, bool loadProfile) =>
        Hosts.GetOrAdd($"{shell}|{loadProfile}", _ => new PowerShellCompletionHost(shell, loadProfile));

    /// <summary>
    /// The whole line as PowerShell would complete its last word (see
    /// <see cref="PowerShellSuggestions.CompleteLine"/>), or null. Null too when the caller
    /// cancels, PowerShell is slow to answer, or it cannot be started.
    /// </summary>
    public async Task<string?> CompleteLineAsync(string text, string cwd, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested || !EnsureStarted() || _process is not { } process)
            {
                return null;
            }

            var id = Interlocked.Increment(ref _nextId);
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = answer;
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, text, cwd })).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _waiting.TryRemove(id, out _);
                Stop();
                return null;
            }

            // Held until PowerShell answers even if the caller gives up, so requests never pile up
            // behind a slow one. The first answer waits on start-up and command discovery.
            var timeout = Task.Delay(_warm ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15), CancellationToken.None);
            var done = await Task.WhenAny(answer.Task, timeout).ConfigureAwait(false);
            _waiting.TryRemove(id, out _);
            if (done != answer.Task)
            {
                // Stuck (a profile waiting on something, a completer that hangs): start afresh next time.
                Stop();
                return null;
            }

            _warm = true;
            return cancellationToken.IsCancellationRequested || await answer.Task.ConfigureAwait(false) is not { } json
                ? null
                : Parse(text, json);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads one answer line's JSON into the completed line.</summary>
    public static string? Parse(string text, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var index = root.TryGetProperty("index", out var i) && i.TryGetInt32(out var iv) ? iv : -1;
            var length = root.TryGetProperty("length", out var l) && l.TryGetInt32(out var lv) ? lv : -1;
            var matches = new List<string>();
            if (root.TryGetProperty("matches", out var m))
            {
                // Windows PowerShell can unroll a one-item array into the item itself.
                if (m.ValueKind == JsonValueKind.Array)
                {
                    matches.AddRange(m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                else if (m.ValueKind == JsonValueKind.String)
                {
                    matches.Add(m.GetString()!);
                }
            }

            return PowerShellSuggestions.CompleteLine(text, index, length, matches);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return true;
        }

        if (_starts >= MaxStarts)
        {
            return false;
        }

        _starts++;
        _warm = false;
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
            var info = new ProcessStartInfo(_shell)
            {
                Arguments = $"-NoLogo -NonInteractive {(_loadProfile ? "" : "-NoProfile ")}-EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }

            _process = process;
            _ = Task.Run(() => ReadAnswersAsync(process));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ReadAnswersAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("@@", StringComparison.Ordinal))
                {
                    continue;
                }

                var json = line[2..];
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id) &&
                        _waiting.TryRemove(id, out var answer))
                    {
                        answer.TrySetResult(json);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        // The process is gone: nobody still waiting gets an answer from it. Waiters already belong
        // to a replacement process when this one was stopped and another started.
        if (Volatile.Read(ref _process) is { } current && !ReferenceEquals(current, process))
        {
            return;
        }

        foreach (var id in _waiting.Keys)
        {
            if (_waiting.TryRemove(id, out var answer))
            {
                answer.TrySetResult(null);
            }
        }
    }

    private void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        process.Dispose();
    }
}