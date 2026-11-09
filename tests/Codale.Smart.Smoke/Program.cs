using Codale.Smart;
if (args.Length < 2) throw new ArgumentException("Usage: smoke <native-directory> <model-path> [cpu|gpu]");
if (!await ModelInstaller.VerifyAsync(args[1], default)) throw new InvalidDataException("Invalid smoke-test model.");
using var runtime = new NativeRuntime(Path.GetFullPath(args[0]));
var watch = System.Diagnostics.Stopwatch.StartNew();
runtime.Load(Path.GetFullPath(args[1]), args.ElementAtOrDefault(2) != "cpu", default);
Console.WriteLine($"Loaded {runtime.Backend} in {watch.Elapsed}.");
var prompt = "<|startoftext|><|im_start|>system\nOutput only the word READY.<|im_end|>\n<|im_start|>user\nConfirm readiness.<|im_end|>\n<|im_start|>assistant\n<think></think>";
var text = runtime.Generate(prompt, 40, default);
Console.WriteLine($"Generated: {text}");
if (!text.Contains("READY", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected model output.");
using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try { runtime.Generate(prompt + new string('x', 4000), 400, cancel.Token); throw new Exception("Cancellation did not stop inference."); }
catch (OperationCanceledException) { Console.WriteLine("Cancellation passed."); }
runtime.Dispose();
await using var smart = new SmartExperience(() => new LoggingRuntime(Path.GetFullPath(args[0]), args.ElementAtOrDefault(2) == "cpu"), (_, _) => Task.FromResult(Path.GetFullPath(args[1])));
await smart.SetEnabledAsync(true);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var intent = await smart.ClassifyAsync("Please plan the migration before implementing anything.", null, deadline.Token);
Console.WriteLine($"Intent: {intent}");
if (intent != SmartIntent.Plan) throw new InvalidDataException("Intent classification failed.");
SmartCandidate[] candidates = [new("1", "theme.cs", "Sets theme colors."), new("2", "git.cs", "Generates git commit messages.")];
var ranking = await smart.RankAsync("git commit message", candidates, deadline.Token);
Console.WriteLine($"Ranking: {string.Join(',', ranking ?? [])}");
if (ranking is null || ranking[0] != "2") throw new InvalidDataException("Candidate ranking failed.");
var commit = await smart.CommitMessageAsync("diff --git a/theme.cs b/theme.cs\n+ public bool DarkMode { get; set; }", deadline.Token);
Console.WriteLine($"Commit: {commit}");
if (string.IsNullOrWhiteSpace(commit)) throw new InvalidDataException("Commit generation failed.");
var summary = await smart.SummarizeAsync("MathHelpers.cs", "public class MathHelpers { public static int Add(int a, int b) => a + b; }", deadline.Token);
Console.WriteLine($"Summary: {summary}");
if (string.IsNullOrWhiteSpace(summary)) throw new InvalidDataException("File summary generation failed.");

sealed class LoggingRuntime(string directory, bool forceCpu) : ISmartRuntime
{
    private readonly NativeRuntime _runtime = new(directory);
    public string Backend => _runtime.Backend;
    public void Load(string path, bool gpu, CancellationToken ct)
    {
        if (forceCpu && gpu) throw new InvalidOperationException("GPU disabled for CPU fallback smoke test.");
        _runtime.Load(path, gpu, ct);
    }
    public string Generate(string prompt, int cap, CancellationToken ct, string? grammar = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { var text = _runtime.Generate(prompt, cap, ct, grammar); Console.WriteLine($"Raw ({watch.Elapsed}): {text}"); return text; }
        catch (Exception ex) { Console.WriteLine(ex); throw; }
    }
    public void Dispose() => _runtime.Dispose();
}
