using Codale.Smart;
try
{
if (args.Length < 2) throw new ArgumentException("Usage: smoke <native-directory> <model-path> [cpu|gpu] [budget-seconds=120] [protocol-only|features-only] [model=lfm3-1.7b-q4]");
if (args.Contains("protocol-only") && args.Contains("features-only")) throw new ArgumentException("Choose one smoke stage.");
var taskBudget = args.FirstOrDefault(a => a.StartsWith("budget-seconds=", StringComparison.Ordinal)) is { } budget
    ? int.Parse(budget.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture) : 120;
var modelId = args.FirstOrDefault(a => a.StartsWith("model=", StringComparison.Ordinal))?["model=".Length..] ?? SmartModels.Default.Id;
var model = SmartModels.All.Single(m => m.Id == modelId);
Console.WriteLine($"Model: {model.Name}; CPU count: {Environment.ProcessorCount}; available memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}");
if (!await ModelInstaller.VerifyAsync(args[1], model, default)) throw new InvalidDataException("Invalid smoke-test model.");
using var runtime = new NativeRuntime(Path.GetFullPath(args[0]));
var watch = System.Diagnostics.Stopwatch.StartNew();
runtime.Configure(model);
runtime.Load(Path.GetFullPath(args[1]), args.ElementAtOrDefault(2) != "cpu", default);
Console.WriteLine($"Loaded {runtime.Backend} in {watch.Elapsed}.");
var readiness = SmartConversation.Create(model, "Output only the word READY.", "Confirm readiness.", false);
var prompt = readiness.Render(runtime, 40, final: true);
var text = runtime.Generate(prompt, 40, default);
Console.WriteLine($"Generated: {text}");
if (!text.Contains("READY", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected model output.");
// Verify the pinned GGUF's actual tool protocol before integrating application callers.
if (!args.Contains("features-only")) foreach (var thinking in new[] { false, true })
{
    using var toolDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var folder = Directory.CreateTempSubdirectory("codale-smart-protocol-");
    try
    {
        var file = Path.Combine(folder.FullName, "answer.txt");
        File.WriteAllText(file, "CODALE_TOOL_ROUNDTRIP_743");
        var conversation = SmartConversation.Create(model,
            "Use read_file to read the requested file. You must call the tool before answering.\nWorking directory: " + folder.FullName,
            "Read answer.txt in the working directory and reply with its contents.", thinking);
        var roundWatch = System.Diagnostics.Stopwatch.StartNew();
        var call = runtime.Generate(conversation.Render(runtime, 2048), 2048, toolDeadline.Token);
        Console.WriteLine($"Tool call latency (thinking={thinking}): {roundWatch.Elapsed}");
        var visible = conversation.Visible(call);
        Console.WriteLine($"Tool protocol (thinking={thinking}): {visible}");
        var calls = conversation.ParseCalls(visible);
        if (calls.Count != 1 || calls[0].Name != "read_file")
            throw new InvalidDataException("Model did not produce the required tool call.");
        var context = new SmartRequestContext(folder.FullName, folder.FullName, "protocol");
        if (!string.Equals(SmartTools.Resolve(context, calls[0].Arguments["path"]), file, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tool call requested the wrong file.");
        var outcome = await new SmartTools().ExecuteAsync(context, calls[0], toolDeadline.Token);
        if (outcome.State != "completed" || outcome.Evidence.Count != 1) throw new InvalidDataException("Tool execution failed.");
        conversation.Add(call, [outcome.Output]);
        roundWatch.Restart();
        var answer = conversation.Visible(runtime.Generate(conversation.Render(runtime, 2048), 2048, toolDeadline.Token));
        Console.WriteLine($"Tool follow-up latency (thinking={thinking}): {roundWatch.Elapsed}");
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
await smart.ConfigureAsync(true, model);
if (smart.Status.State != SmartState.Ready) throw new InvalidDataException(smart.Status.Message);
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
        SmartRequestContext Context(string task) => new(fixture.FullName, fixture.FullName, task, Deadline: DateTimeOffset.UtcNow.AddSeconds(task == "summary" ? Math.Min(20, taskBudget) : taskBudget));
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
        if (!commit.Evidence.Any(e => e.Kind == "command" && e.Command?.Contains("git diff --cached", StringComparison.Ordinal) == true))
            throw new InvalidDataException("Commit generation did not inspect the staged diff.");
        var summary = await smart.SummarizeAsync(Context("summary"), "MathHelpers.cs");
        Console.WriteLine($"Summary: {summary?.Value}");
        var shell = await smart.ShellCommandAsync(Context("shell"), "show git status", "powershell.exe");
        Console.WriteLine($"Shell command: {shell?.Value}");
        if (string.IsNullOrWhiteSpace(shell?.Value)) throw new InvalidDataException("Shell translation failed.");
        var brief = await smart.BriefAsync(Context("brief"), "Explain the addition helper", SmartIntent.Ask,
            "MathHelpers.cs:1-1\npublic class MathHelpers { public static int Add(int a, int b) => a + b; }");
        Console.WriteLine($"Brief: {brief?.Value}");
        if (string.IsNullOrWhiteSpace(brief?.Value)) throw new InvalidDataException("Brief generation failed.");
        if (string.IsNullOrWhiteSpace(summary?.Value) || !summary.Evidence.Any(e => e.Kind == "file"))
            throw new InvalidDataException("File summary tool round trip failed.");
    }
}
finally
{
    foreach (var file in fixture.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
    fixture.Delete(true);
}

}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
finally
{
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    process.Refresh();
    Console.WriteLine($"Peak working set: {process.PeakWorkingSet64}; peak private bytes: {process.PeakPagedMemorySize64}");
}

sealed class LoggingRuntime(string directory, bool forceCpu) : ISmartRuntime
{
    private readonly NativeRuntime _runtime = new(directory);
    public string Backend => _runtime.Backend;
    public void Configure(SmartModel model) => _runtime.Configure(model);
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
