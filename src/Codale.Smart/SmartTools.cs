using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codale.Smart;

internal sealed record SmartToolOutcome(string Output, IReadOnlyList<SmartEvidence> Evidence,
    int? ExitCode = null, string State = "completed");

internal sealed class SmartTools(TimeSpan? commandTimeout = null)
{
    public const int OutputBytes = 4096;
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private static object Schema(string name, string description, object properties, string[] required) =>
        new { name, description, parameters = new { type = "object", properties, required, additionalProperties = false } };
    internal static readonly object[] Definitions =
    {
        Schema("list_directory", "List files and directories; path defaults to the working directory.", new { path = new { type = "string" } }, []),
        Schema("search_files", "Find file paths matching a name, substring or glob. Set content=true to search literal file contents. Bounded results; narrow the path if truncated.",
            new { path = new { type = "string" }, pattern = new { type = "string" }, content = new { type = "boolean" } }, ["pattern"]),
        Schema("read_file", "Read a UTF-8 text file, with optional 1-based start_line and line_count (default 80).", new
        { path = new { type = "string" }, start_line = new { type = "integer" }, line_count = new { type = "integer" } }, ["path"]),
        Schema("run_shell", "Execute a noninteractive shell command. Default cwd is the active worktree. Commands may change files. Maximum 60 seconds and bounded output.",
            new { command = new { type = "string" }, cwd = new { type = "string" } }, ["command"])
    };
    public static readonly string Schemas = "<tools>\n" + string.Join("\n", Definitions.Select(s => JsonSerializer.Serialize(s))) + "\n</tools>";
    internal static readonly object[] ContextDefinitions =
    [
        Schema("query_index", "Search the project index for relevant indexed source using alternative terms.",
            new { query = new { type = "string" } }, ["query"]),
        Schema("read_index", "Inspect the indexed source excerpt for a candidate ID returned by the index.",
            new { id = new { type = "string" } }, ["id"])
    ];

    public static string Resolve(SmartRequestContext context, string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, context.WorkingDirectory);

    public async Task<SmartToolOutcome> ExecuteAsync(SmartRequestContext context, SmartToolCall call, CancellationToken ct, Action<string>? progress = null)
    {
        try
        {
            var allowed = call.Name switch
            {
                "list_directory" => new[] { "path" },
                "search_files" => ["path", "pattern", "content"],
                "read_file" => ["path", "start_line", "line_count"],
                "run_shell" => ["command", "cwd"],
                _ => throw new InvalidDataException("Unknown tool: " + call.Name)
            };
            if (call.Arguments.Keys.Any(k => !allowed.Contains(k))) throw new InvalidDataException("Unknown argument.");
            string? Optional(string key) => call.Arguments.GetValueOrDefault(key);
            string Required(string key) => !string.IsNullOrWhiteSpace(Optional(key)) ? Optional(key)! : throw new InvalidDataException("Missing " + key);
            ct.ThrowIfCancellationRequested();
            var path = Resolve(context, Optional("path"));
            switch (call.Name)
            {
                case "list_directory":
                    var listing = new StringBuilder(path + "\n");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        listing.AppendLine(Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""));
                        if (listing.Length > OutputBytes) { listing.AppendLine("[more entries omitted]"); break; }
                    }
                    return Outcome(listing.ToString());
                case "read_file":
                    int Number(string key, int fallback, int max) => Optional(key) is not { } value ? fallback
                        : int.TryParse(value, out var n) && n > 0 && n <= max ? n : throw new InvalidDataException("Invalid " + key);
                    var evidence = await ReadAsync(path, Number("start_line", 1, 1_000_000), Number("line_count", 80, 500), ct).ConfigureAwait(false);
                    return Outcome($"{path}:{evidence.StartLine}-{evidence.EndLine}\n{evidence.Text}", [evidence]);
                case "search_files":
                    var pattern = Required("pattern");
                    if (Optional("content") is { } flag && !bool.TryParse(flag, out _)) throw new InvalidDataException("content must be true or false.");
                    return await SearchAsync(path, pattern, Optional("content") == "true", ct).ConfigureAwait(false);
                default:
                    return await ShellAsync(Required("command"), Resolve(context, Optional("cwd")), ct, progress, commandTimeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Outcome(ex.Message, state: "error"); }
    }

    private static SmartToolOutcome Outcome(string text, IReadOnlyList<SmartEvidence>? evidence = null, int? exitCode = null, string state = "completed")
    {
        text = SmartConversation.Clip(text);
        string Encode() => SmartConversation.Result(new { state, exitCode, text });
        var encoded = Encode();
        while (Encoding.UTF8.GetByteCount(encoded) > OutputBytes)
        { text = SmartConversation.Clip(text, Encoding.UTF8.GetByteCount(text) / 2); encoded = Encode(); }
        return new(encoded, evidence ?? [], exitCode, state);
    }

    internal static async Task<SmartEvidence> ReadAsync(string path, int start, int count, CancellationToken ct)
    {
        byte[] bytes;
        await using (var stream = File.OpenRead(path))
        {
            if (stream.Length > MaxFileBytes) throw new IOException("File exceeds the 16 MiB text inspection limit; use shell for other formats.");
            bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (stream.ReadByte() != -1) throw new IOException("File grew during inspection; retry the read.");
        }
        var source = Encoding.UTF8.GetString(bytes);
        if (source.Contains('\0')) throw new IOException("Binary file; use shell to inspect it.");
        var lines = new StringBuilder();
        using var reader = new StringReader(source.TrimStart('\uFEFF'));
        int line = 0, end = start - 1;
        while (reader.ReadLine() is { } value)
        {
            ct.ThrowIfCancellationRequested();
            line++;
            if (line < start) continue;
            if (line >= start + count) break;
            lines.AppendLine(value); end = line;
            if (Encoding.UTF8.GetByteCount(lines.ToString()) >= OutputBytes) break;
        }
        return new("file", Path.GetFullPath(path), SmartConversation.Clip(lines.ToString()), DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(bytes)), start, Math.Max(start, end));
    }

    internal static async Task<bool> FreshAsync(SmartEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind != "file") return true;
        try
        {
            if (new FileInfo(evidence.Path).Length > MaxFileBytes) return false;
            await using var stream = File.OpenRead(evidence.Path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) == evidence.Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<SmartToolOutcome> SearchAsync(string path, string pattern, bool content, CancellationToken ct)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var output = new StringBuilder();
        var evidence = new List<SmartEvidence>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        int scanned = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned > 5000 || output.Length > OutputBytes) { output.AppendLine("[search truncated; narrow the path]"); break; }
            if (!content)
            {
                if (file.Contains(pattern, StringComparison.OrdinalIgnoreCase) || FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), true))
                    output.AppendLine(file);
                continue;
            }
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                using var reader = File.OpenText(file);
                int line = 0, chars = 0;
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } value)
                {
                    chars += value.Length;
                    if (chars > 1024 * 1024 || value.Contains('\0')) break;
                    line++;
                    if (!value.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                    var hit = await ReadAsync(file, line, 3, ct).ConfigureAwait(false);
                    evidence.Add(hit); output.AppendLine($"{file}:{line}\n{hit.Text}"); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Outcome(output.Length == 0 ? "No matches." : output.ToString(), evidence);
    }

    private static async Task<SmartToolOutcome> ShellAsync(string command, string cwd, CancellationToken ct, Action<string>? progress, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh", WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command" }) info.ArgumentList.Add(arg);
            info.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);
        }
        else { info.ArgumentList.Add("-c"); info.ArgumentList.Add(command); }
        using var process = new Process { StartInfo = info };
        process.Start(); process.StandardInput.Close();
        using var job = Codale.Core.Tasks.ProcessJob.TryCreate(process);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var registration = deadline.Token.Register(() =>
        {
            if (job?.Terminate() == true) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });
        var progressGate = new object();
        string partialOut = "", partialError = "";
        var lastProgress = Stopwatch.StartNew();
        bool firstProgress = true;
        void Report(bool error, string value)
        {
            if (progress is null) return;
            lock (progressGate)
            {
                if (error) partialError = value; else partialOut = value;
                if (!firstProgress && lastProgress.ElapsedMilliseconds < 150) return;
                firstProgress = false; lastProgress.Restart();
                progress(SmartConversation.Clip($"stdout:\n{partialOut}\nstderr:\n{partialError}"));
            }
        }
        async Task<string> Drain(StreamReader reader, bool error)
        {
            var output = new StringBuilder(); var buffer = new char[1024]; bool truncated = false;
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var left = 2048 - output.Length;
                if (left > 0) output.Append(buffer, 0, Math.Min(left, read));
                if (read > left) truncated = true;
                if (left > 0) Report(error, output.ToString());
            }
            return output + (truncated ? "\n[truncated]" : "");
        }
        var stdout = Drain(process.StandardOutput, false); var stderr = Drain(process.StandardError, true);
        var state = "completed";
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { state = ct.IsCancellationRequested ? "cancelled" : "timed out"; }
        // Cancellation kills the tree before waiting for the final pipe contents.
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = SmartConversation.Clip($"stdout:\n{await stdout.ConfigureAwait(false)}\nstderr:\n{await stderr.ConfigureAwait(false)}");
        var observation = new SmartEvidence("command", cwd, text, DateTimeOffset.UtcNow, Command: command, ExitCode: process.ExitCode);
        if (state == "completed" && process.ExitCode != 0) state = "error";
        return Outcome(text, [observation], process.ExitCode, state);
    }
}
