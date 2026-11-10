using Codale.Smart;
if (args.Length < 2) throw new ArgumentException("Usage: smoke <native-directory> <model-path> [cpu|gpu] [budget-seconds=120] [protocol-only]");
var taskBudget = args.FirstOrDefault(a => a.StartsWith("budget-seconds=", StringComparison.Ordinal)) is { } budget
    ? int.Parse(budget.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture) : 120;
if (!await ModelInstaller.VerifyAsync(args[1], default)) throw new InvalidDataException("Invalid smoke-test model.");
using var runtime = new NativeRuntime(Path.GetFullPath(args[0]));
var watch = System.Diagnostics.Stopwatch.StartNew();
runtime.Load(Path.GetFullPath(args[1]), args.ElementAtOrDefault(2) != "cpu", default);
Console.WriteLine($"Loaded {runtime.Backend} in {watch.Elapsed}.");
var prompt = "<|startoftext|><|im_start|>system\nOutput only the word READY.<|im_end|>\n<|im_start|>user\nConfirm readiness.<|im_end|>\n<|im_start|>assistant\n<think></think>";
var text = runtime.Generate(prompt, 40, default);
Console.WriteLine($"Generated: {text}");
if (!text.Contains("READY", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected model output.");
// Verify the pinned GGUF's actual tool protocol before integrating application callers.
foreach (var thinking in new[] { false, true })
{
    using var toolDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var folder = Directory.CreateTempSubdirectory("codale-smart-protocol-");
    try
    {
        var file = Path.Combine(folder.FullName, "answer.txt");
        File.WriteAllText(file, "CODALE_TOOL_ROUNDTRIP_743");
        var head = "<|startoftext|><|im_start|>system\nUse the read_file tool to read the requested file. " +
            "You must call the tool before answering.\n<tools>\n" +
            "{\"name\":\"read_file\",\"description\":\"Read a text file\",\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}}" +
            "\n</tools><|im_end|>\n<|im_start|>user\nRead " + file.Replace('\\', '/') +
            " and reply with its contents.<|im_end|>\n<|im_start|>assistant\n";
        var prefix = thinking ? "<think>" : "<think></think>";
        var call = runtime.Generate(head + prefix, 2048, toolDeadline.Token);
        var visible = SmartExperience.Clean(prefix + call);
        Console.WriteLine($"Tool protocol (thinking={thinking}): {visible}");
        if (!visible.Contains("<function=read_file>") || !visible.Contains("</tool_call>"))
            throw new InvalidDataException("Pinned model did not produce the required tool call.");
        var next = head + prefix + call + "<|im_end|>\n<|im_start|>tool\n<tool_response>\n" +
            File.ReadAllText(file) + "\n</tool_response><|im_end|>\n<|im_start|>assistant\n" + prefix;
        var answer = SmartExperience.Clean(prefix + runtime.Generate(next, 2048, toolDeadline.Token));
        Console.WriteLine($"Tool follow-up: {answer}");
        if (!answer.Contains("CODALE_TOOL_ROUNDTRIP_743")) throw new InvalidDataException("Tool result was not used.");
    }
    finally { folder.Delete(true); }
}
if (args.Contains("protocol-only")) return;
using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try { runtime.Generate(prompt + new string('x', 4000), 400, cancel.Token); throw new Exception("Cancellation did not stop inference."); }
catch (OperationCanceledException) { Console.WriteLine("Cancellation passed."); }
runtime.Dispose();
await using var smart = new SmartExperience(() => new LoggingRuntime(Path.GetFullPath(args[0]), args.ElementAtOrDefault(2) == "cpu"), (_, _) => Task.FromResult(Path.GetFullPath(args[1])));
await smart.SetEnabledAsync(true);
var loggedTools = new HashSet<string>();
string? smokeProject = null;
smart.ActivityChanged += (_, _) =>
{
    // Tool failures must remain visible when diagnosing a real-model smoke failure.
    foreach (var run in smart.Activities(smokeProject ?? Environment.CurrentDirectory))
        foreach (var tool in run.Tools ?? [])
            if (tool.State != "running" && loggedTools.Add(run.Id + tool.StartedAt.ToString("O"))) Console.WriteLine($"Tool {tool.Tool}: {tool.Output}");
};
var fixture = Directory.CreateTempSubdirectory("codale-smart-smoke-");
smokeProject = fixture.FullName;
try
{
    File.WriteAllText(Path.Combine(fixture.FullName, "theme.cs"), "public class Theme { public bool DarkMode { get; set; } }");
    File.WriteAllText(Path.Combine(fixture.FullName, "git.cs"), "// Generates git commit messages from staged changes.");
    File.WriteAllText(Path.Combine(fixture.FullName, "MathHelpers.cs"), "public class MathHelpers { public static int Add(int a, int b) => a + b; }");
    void Git(params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = fixture.FullName, CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!; process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("Smoke git fixture failed.");
    }
    Git("init", "--quiet"); Git("add", ".");
    foreach (var thinking in new[] { false, true })
    {
        smart.Thinking = thinking;
        SmartRequestContext Context(string task) => new(fixture.FullName, fixture.FullName, task, Deadline: DateTimeOffset.UtcNow.AddSeconds(taskBudget));
        var intent = await smart.ClassifyAsync(Context("routing"), "Please plan the migration before implementing anything.", null);
        Console.WriteLine($"Intent (thinking={thinking}): {intent?.Value}");
        if (intent?.Value != SmartIntent.Plan) throw new InvalidDataException("Intent classification failed.");
        SmartCandidate[] candidates = [new("1", "theme.cs"), new("2", "git.cs")];
        var ranking = await smart.RankAsync(Context("context"), "git commit message; inspect the candidate files", candidates);
        Console.WriteLine($"Ranking: {string.Join(',', ranking?.Value ?? [])}");
        if (ranking?.Value is null || ranking.Value[0] != "2") throw new InvalidDataException("Candidate ranking failed.");
        var commit = await smart.CommitMessageAsync(Context("commit"));
        Console.WriteLine($"Commit: {commit?.Value}");
        if (string.IsNullOrWhiteSpace(commit?.Value)) throw new InvalidDataException("Commit generation failed.");
        var summary = await smart.SummarizeAsync(Context("summary"), "MathHelpers.cs");
        Console.WriteLine($"Summary: {summary?.Value}");
        if (string.IsNullOrWhiteSpace(summary?.Value) || !summary.Evidence.Any(e => e.Kind == "file"))
            throw new InvalidDataException("File summary tool round trip failed.");
    }
}
finally
{
    foreach (var file in fixture.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
    fixture.Delete(true);
}

sealed class LoggingRuntime(string directory, bool forceCpu) : ISmartRuntime
{
    private readonly NativeRuntime _runtime = new(directory);
    public string Backend => _runtime.Backend;
    public int CountTokens(string prompt) => _runtime.CountTokens(prompt);
    public void Load(string path, bool gpu, CancellationToken ct)
    {
        if (forceCpu && gpu) throw new InvalidOperationException("GPU disabled for CPU fallback smoke test.");
        _runtime.Load(path, gpu, ct);
    }
    public string Generate(string prompt, int cap, CancellationToken ct, string? grammar = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { var text = _runtime.Generate(prompt, cap, ct, grammar); Console.WriteLine($"Generated ({watch.Elapsed}, {text.Length} chars): {text[..Math.Min(text.Length, 600)]}"); return text; }
        catch (Exception ex) { Console.WriteLine(ex); throw; }
    }
    public void Dispose() => _runtime.Dispose();
}
