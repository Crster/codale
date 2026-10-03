using System.Text.Json;
using System.Text.Json.Nodes;

using Codale.Core.Tasks;
using Codale.Mcp.Tasks;

namespace Codale.Mcp.Tests;

/// <summary>The output hook, the read guard and the explore/ask_files tools, without a CLI or a model.</summary>
public sealed class TokenSaverHooksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codale-hooks-" + Guid.NewGuid().ToString("N"));

    public TokenSaverHooksTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeAssist : ITaskAssist
    {
        public string? Digest;
        public (long Before, long After)? Saved;
        public string? LastQuestion;
        public IReadOnlyList<string>? LastPaths;

        public Task<string> ExploreAsync(string question, CancellationToken ct)
        {
            LastQuestion = question;
            return Task.FromResult("It is in AppSettings.cs:340-360.");
        }

        public Task<string> AskFilesAsync(string question, IReadOnlyList<string> paths, CancellationToken ct)
        {
            LastQuestion = question;
            LastPaths = paths;
            return Task.FromResult("answer");
        }

        public Task<string?> DigestAsync(string command, string output, CancellationToken ct) => Task.FromResult(Digest);

        public void RecordSaved(long beforeChars, long afterChars) => Saved = (beforeChars, afterChars);
    }

    private static string Payload(string command, string stdout, string stderr = "") =>
        new JsonObject
        {
            ["session_id"] = "s1",
            ["tool_use_id"] = "toolu_1",
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = command },
            ["tool_response"] = new JsonObject { ["stdout"] = stdout, ["stderr"] = stderr, ["interrupted"] = false, ["isImage"] = false },
        }.ToJsonString();

    private static string LongTestLog(int passing) =>
        string.Join('\n', Enumerable.Range(1, passing).Select(i => $"  Passed Suite.Case{i} [1 ms]")) +
        "\n  Failed Suite.Broken [2 ms]\nTotal tests: 1000";

    [Fact]
    public async Task Output_hook_replaces_only_stdout_and_stderr_and_keeps_the_full_output()
    {
        var assist = new FakeAssist();
        var reply = await ShellOutputHook.EvaluateAsync(Payload("dotnet test", LongTestLog(300)), assist, _dir, digest: false);

        var output = JsonNode.Parse(reply!)!["hookSpecificOutput"]!;
        Assert.Equal("PostToolUse", output["hookEventName"]!.GetValue<string>());
        var updated = output["updatedToolOutput"]!.AsObject();
        Assert.False(updated["interrupted"]!.GetValue<bool>());
        Assert.False(updated["isImage"]!.GetValue<bool>());

        var stdout = updated["stdout"]!.GetValue<string>();
        Assert.Contains("Failed Suite.Broken", stdout);
        Assert.DoesNotContain("Case17 ", stdout);

        var path = System.Text.RegularExpressions.Regex.Match(stdout, @"Full output: (.+?\.log)").Groups[1].Value;
        Assert.Contains("Case17 ", File.ReadAllText(path));
        Assert.NotNull(assist.Saved);
        Assert.True(assist.Saved!.Value.After < assist.Saved.Value.Before);
    }

    [Fact]
    public async Task Small_output_is_left_alone() =>
        Assert.Null(await ShellOutputHook.EvaluateAsync(Payload("git status", "nothing to commit"), null, _dir));

    [Fact]
    public async Task Malformed_payload_is_left_alone() =>
        Assert.Null(await ShellOutputHook.EvaluateAsync("{not json", null, _dir));

    [Fact]
    public async Task Output_still_too_long_is_digested_when_asked()
    {
        var unique = string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"{Guid.NewGuid()} unrelated text {i % 7} here"));
        var assist = new FakeAssist { Digest = "DIGEST: 1 failure in Foo.cs:12" };

        var reply = await ShellOutputHook.EvaluateAsync(Payload("./run.sh", unique), assist, _dir, digest: true);

        var stdout = JsonNode.Parse(reply!)!["hookSpecificOutput"]!["updatedToolOutput"]!["stdout"]!.GetValue<string>();
        Assert.StartsWith("DIGEST: 1 failure in Foo.cs:12", stdout);
        Assert.Contains("summarised", stdout);
    }

    [Fact]
    public async Task Output_still_too_long_is_cut_without_a_digest()
    {
        var unique = string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"{Guid.NewGuid()} unrelated text {i % 7} here"));

        var reply = await ShellOutputHook.EvaluateAsync(Payload("./run.sh", unique), new FakeAssist(), _dir, digest: false);

        var stdout = JsonNode.Parse(reply!)!["hookSpecificOutput"]!["updatedToolOutput"]!["stdout"]!.GetValue<string>();
        Assert.Contains("lines omitted", stdout);
        Assert.True(stdout.Length < unique.Length / 5);
    }

    private string ReadPayload(string file, JsonObject? extra = null)
    {
        var input = new JsonObject { ["file_path"] = file };
        foreach (var (key, value) in extra ?? [])
        {
            input[key] = value?.DeepClone();
        }

        return new JsonObject { ["session_id"] = "s1", ["tool_name"] = "Read", ["tool_input"] = input }.ToJsonString();
    }

    private string WriteLines(string name, int count)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(i => $"line {i}"));
        return path;
    }

    [Fact]
    public void Read_guard_turns_away_the_first_whole_read_of_a_large_file_only()
    {
        var big = WriteLines("Big.cs", 900);
        var state = Path.Combine(_dir, "state");

        var first = ReadGuard.Evaluate(ReadPayload(big), state, offerAskFiles: true);
        var reason = JsonNode.Parse(first!)!["hookSpecificOutput"]!;
        Assert.Equal("deny", reason["permissionDecision"]!.GetValue<string>());
        Assert.StartsWith(ReadGuard.ReasonPrefix, reason["permissionDecisionReason"]!.GetValue<string>());
        Assert.Contains("900 lines", reason["permissionDecisionReason"]!.GetValue<string>());
        Assert.Contains("ask_files", reason["permissionDecisionReason"]!.GetValue<string>());

        // Asking again is deliberate.
        Assert.Null(ReadGuard.Evaluate(ReadPayload(big), state, offerAskFiles: true));
    }

    [Fact]
    public void Read_guard_lets_ranged_small_and_binary_reads_through()
    {
        var big = WriteLines("Big2.cs", 900);
        var small = WriteLines("Small.cs", 50);
        var image = WriteLines("shot.png", 900);
        var state = Path.Combine(_dir, "state");

        Assert.Null(ReadGuard.Evaluate(ReadPayload(big, new JsonObject { ["offset"] = 100, ["limit"] = 50 }), state));
        Assert.Null(ReadGuard.Evaluate(ReadPayload(small), state));
        Assert.Null(ReadGuard.Evaluate(ReadPayload(image), state));
        Assert.Null(ReadGuard.Evaluate(ReadPayload(Path.Combine(_dir, "missing.cs")), state));
    }

    [Fact]
    public void Read_guard_without_ask_files_does_not_mention_it()
    {
        var reply = ReadGuard.Evaluate(ReadPayload(WriteLines("Big3.cs", 900)), Path.Combine(_dir, "state"), offerAskFiles: false);
        Assert.DoesNotContain("ask_files", reply);
    }

    [Fact]
    public void Shell_guard_refuses_a_background_launch_and_tolerates_odd_payloads()
    {
        var denied = BackgroundShellGuard.Evaluate("""{"tool_name":"Bash","tool_input":{"command":"x","run_in_background":true}}""");
        Assert.Equal("deny", JsonNode.Parse(denied!)!["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());

        Assert.Null(BackgroundShellGuard.Evaluate("""{"tool_name":"Bash","tool_input":{"command":"x"}}"""));
        Assert.Null(BackgroundShellGuard.Evaluate("""{"tool_name":42,"tool_input":{"run_in_background":true}}"""));
        Assert.Null(BackgroundShellGuard.Evaluate("""{"tool_name":null,"tool_input":{"run_in_background":true}}"""));
        Assert.Null(BackgroundShellGuard.Evaluate("""{"tool_name":"Bash","tool_input":"nope"}"""));
        Assert.Null(BackgroundShellGuard.Evaluate("""{"tool_name":"Bash"}"""));
        Assert.Null(BackgroundShellGuard.Evaluate("[1,2]"));
        Assert.Null(BackgroundShellGuard.Evaluate("{not json"));
        Assert.Null(BackgroundShellGuard.Evaluate(""));
    }

    [Fact]
    public void Read_guard_tolerates_odd_payloads()
    {
        var state = Path.Combine(_dir, "state-odd");

        Assert.Null(ReadGuard.Evaluate("""{"tool_name":7,"tool_input":{"file_path":"a.cs"}}""", state));
        Assert.Null(ReadGuard.Evaluate("""{"tool_name":"Read","tool_input":{"file_path":12}}""", state));
        Assert.Null(ReadGuard.Evaluate("""{"tool_name":"Read","session_id":5,"tool_input":{"file_path":"a.cs"}}""", state));
        Assert.Null(ReadGuard.Evaluate("[]", state));
        Assert.Null(ReadGuard.Evaluate("{not json", state));
        Assert.False(ReadGuard.RememberRefusal(state, "s", "bad\0path"));
    }

    [Fact]
    public void Saved_output_is_capped_keeping_head_and_tail()
    {
        Assert.Equal("short", ShellOutputHook.Cap("short", 100));

        var capped = ShellOutputHook.Cap(new string('a', 1000) + new string('z', 1000), 100);
        Assert.True(capped.Length < 300);
        Assert.StartsWith(new string('a', 50), capped);
        Assert.EndsWith(new string('z', 50), capped);
        Assert.Contains("omitted 1,900 characters", capped);

        var huge = new string('x', ShellOutputHook.MaxSavedChars + 10_000);
        var path = ShellOutputHook.SaveFull(Path.Combine(_dir, "big-out"), "s1", "t1", huge, "");
        Assert.True(new FileInfo(path!).Length < ShellOutputHook.MaxSavedChars + 1_000);
        Assert.Contains("omitted", File.ReadAllText(path!));
    }

    [Fact]
    public void Stale_saved_outputs_and_guard_state_are_swept_but_fresh_ones_kept()
    {
        var root = Path.Combine(_dir, "sweep");
        var oldDir = Directory.CreateDirectory(Path.Combine(root, "old-session"));
        File.WriteAllText(Path.Combine(oldDir.FullName, "a.log"), "old");
        oldDir.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-10);
        var oldFile = Path.Combine(root, "old.txt");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
        var freshFile = Path.Combine(root, "fresh.txt");
        File.WriteAllText(freshFile, "new");

        ShellOutputHook.SweepStale(root, ShellOutputHook.KeepFor);

        Assert.False(Directory.Exists(oldDir.FullName));
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));

        // Throttled: an immediate second sweep does not rescan.
        File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow.AddDays(-10));
        ShellOutputHook.SweepStale(root, ShellOutputHook.KeepFor);
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task Concurrent_add_command_calls_all_land()
    {
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        var server = new McpServer("t", "1", TaskTools.Create(new NoTasks(), projectRoot: project));

        var replies = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => server.HandleLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"tools/call\",\"params\":{\"name\":\"add_command\"," +
            "\"arguments\":{\"name\":\"cmd" + i + "\",\"command\":\"echo " + i + "\"}}}")));

        Assert.All(replies, r => Assert.DoesNotContain("\"isError\":true", r!.ToJsonString()));
        var names = Codale.Commands.CommandCatalog.Discover(project).Select(c => c.Name).ToHashSet();
        Assert.All(Enumerable.Range(0, 16), i => Assert.Contains($"cmd{i}", names));
    }

    private sealed class NoTasks : ITaskService
    {
        public Task<TaskSnapshot> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<TaskSnapshot> ReadAsync(string id, long? since, int? tailChars, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSnapshot>>([]);
        public Task<TaskSnapshot> StopAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<List<string?>> ToolNames(McpServer server)
    {
        var reply = await server.HandleLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        return JsonDocument.Parse(reply!.ToJsonString()).RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
    }

    [Fact]
    public async Task Assist_tools_appear_only_with_an_assist()
    {
        Assert.DoesNotContain("explore", await ToolNames(new McpServer("t", "1", TaskTools.Create(new NoTasks()))));

        var names = await ToolNames(new McpServer("t", "1", TaskTools.Create(new NoTasks(), assist: new FakeAssist())));
        Assert.Contains("explore", names);
        Assert.Contains("ask_files", names);
    }

    [Fact]
    public async Task Ask_files_passes_the_question_and_paths()
    {
        var assist = new FakeAssist();
        var server = new McpServer("t", "1", TaskTools.Create(new NoTasks(), assist: assist));

        var reply = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ask_files","arguments":{"question":"what does it do?","paths":["a.cs","b.cs"]}}}""");

        Assert.Contains("answer", reply!.ToJsonString());
        Assert.Equal("what does it do?", assist.LastQuestion);
        Assert.Equal(["a.cs", "b.cs"], assist.LastPaths);
    }

    [Fact]
    public async Task Pipe_server_answers_assist_ops_and_refuses_them_without_one()
    {
        var with = new TaskPipeServer(new NoTasks(), new FakeAssist { Digest = "short" });
        var reply = await with.HandleAsync($$"""{"token":"{{with.Token}}","op":"explore","question":"where?"}""");
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal("It is in AppSettings.cs:340-360.", reply["text"]!.GetValue<string>());

        var digest = await with.HandleAsync($$"""{"token":"{{with.Token}}","op":"digest","command":"x","output":"y"}""");
        Assert.Equal("short", digest["text"]!.GetValue<string>());

        var without = new TaskPipeServer(new NoTasks());
        var refused = await without.HandleAsync($$"""{"token":"{{without.Token}}","op":"explore","question":"where?"}""");
        Assert.False(refused["ok"]!.GetValue<bool>());
    }
}
