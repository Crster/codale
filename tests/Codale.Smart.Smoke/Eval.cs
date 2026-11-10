using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codale.Smart;

/// <summary>
/// Real-model evaluation of every Smart scenario: labelled cases, three seeded runs each at the recommended sampling,
/// accuracy and latency against the pass bars, written as a markdown report.
/// </summary>
static class Eval
{
    private static readonly uint[] Seeds = [1, 2, 3];

    private sealed record Row(string Scenario, int Passed, int Total, List<long> Latencies, string Bar, bool Ok, List<string> Failures, string Notes = "");

    public static async Task<bool> RunAsync(string nativeDirectory, string modelPath, SmartModel model, string reportPath, bool gpu)
    {
        await using var smart = new SmartExperience(() => new NativeRuntime(nativeDirectory), (_, _) => Task.FromResult(modelPath));
        await smart.ConfigureAsync(true, model);
        if (!gpu && smart.Status.Backend != "CPU") Console.WriteLine("Note: GPU loaded although cpu was requested.");
        if (smart.Status.State != SmartState.Ready) throw new InvalidDataException(smart.Status.Message);
        var root = Directory.CreateTempSubdirectory("codale-smart-eval-");
        var rows = new List<Row>();
        try
        {
            rows.Add(await RoutingAsync(smart, root.FullName));
            rows.Add(await ShellAsync(smart, root.FullName));
            rows.Add(await AssessAsync(smart, root.FullName, thinking: false));
            rows.Add(await AssessAsync(smart, root.FullName, thinking: true));
            rows.Add(await CommitAsync(smart, root.FullName, thinking: false));
            rows.Add(await CommitAsync(smart, root.FullName, thinking: true));
            rows.Add(await FimAsync(smart, root.FullName));
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            root.Delete(true);
        }
        var report = new StringBuilder($"# Granite 4.2 Smart evaluation\n\n{model.Name} · {smart.Status.Backend} · {DateTime.Now:yyyy-MM-dd HH:mm} · " +
            "temperature 1.0, top_p 0.95, seeds 1–3\n\n| Scenario | Passed | Accuracy | p50 | p95 | Bar | Result |\n|---|---|---|---|---|---|---|\n");
        foreach (var row in rows)
            report.Append($"| {row.Scenario} | {row.Passed}/{row.Total} | {100.0 * row.Passed / Math.Max(1, row.Total):0}% | {Percentile(row.Latencies, 0.5)} ms | {Percentile(row.Latencies, 0.95)} ms | {row.Bar} | {(row.Ok ? "PASS" : "FAIL")} |\n");
        foreach (var row in rows.Where(r => r.Failures.Count > 0 || r.Notes.Length > 0))
        {
            report.Append($"\n## {row.Scenario}\n\n");
            if (row.Notes.Length > 0) report.Append(row.Notes).Append("\n\n");
            foreach (var failure in row.Failures.Take(15)) report.Append("- ").Append(failure.Replace("|", "\\|")).Append('\n');
        }
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine(report);
        return rows.All(r => r.Ok);
    }

    private static long Percentile(List<long> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    }

    private static SmartRequestContext Context(string root, string task) => new(root, root, task);

    // ---------------------------------------------------------------- routing

    private static readonly (string Message, SmartIntent Intent)[] RoutingCases =
    [
        ("how does the cache work?", SmartIntent.Ask), ("what does Signal mean here?", SmartIntent.Ask),
        ("why does the terminal flicker?", SmartIntent.Ask), ("what does SendCoreAsync do", SmartIntent.Ask),
        ("show me how the router works", SmartIntent.Ask), ("how does the router decide?", SmartIntent.Ask),
        ("what is the default mode", SmartIntent.Ask), ("can you explain the todo flow", SmartIntent.Ask),
        ("does the todo depend on the mode?", SmartIntent.Ask), ("review the parser", SmartIntent.Ask),
        ("how do I fix login?", SmartIntent.Ask), ("can you explain the router?", SmartIntent.Ask),
        ("where is the commit message generated?", SmartIntent.Ask), ("compare the two caching strategies we use", SmartIntent.Ask),
        ("implement login form", SmartIntent.Plan), ("migrate everything to the new framework", SmartIntent.Plan),
        ("make the app better", SmartIntent.Plan), ("plan the migration", SmartIntent.Plan),
        ("write an implementation plan", SmartIntent.Plan), ("fix the router but do not edit yet; propose a plan first", SmartIntent.Plan),
        ("plan the migration across all services before doing anything", SmartIntent.Plan), ("add authentication to the whole app", SmartIntent.Plan),
        ("rewrite the backend in Rust", SmartIntent.Plan), ("design a notifications system first, then we'll build it", SmartIntent.Plan),
        ("plan and implement the migration", SmartIntent.Plan), ("build a checkout flow", SmartIntent.Plan),
        ("add a delete button to each row", SmartIntent.Act), ("the header is ugly", SmartIntent.Act),
        ("is this correct? the totals are wrong", SmartIntent.Act), ("also do the same for the footer", SmartIntent.Act),
        ("fix the login redirect", SmartIntent.Act), ("can you fix the login redirect?", SmartIntent.Act),
        ("please add a status column", SmartIntent.Act), ("yes go ahead", SmartIntent.Act),
        ("it still crashes on start", SmartIntent.Act), ("revert the last change", SmartIntent.Act),
        ("rename UserService to AccountService", SmartIntent.Act), ("why is login slow, fix it", SmartIntent.Act),
        ("implement the plan without changing the database", SmartIntent.Act), ("commit the staged changes", SmartIntent.Act),
        ("run the tests", SmartIntent.Act), ("the file list has a dot before every name", SmartIntent.Act),
    ];

    private static async Task<Row> RoutingAsync(SmartExperience smart, string root)
    {
        smart.Thinking = false;
        int passed = 0, total = 0; var latencies = new List<long>(); var failures = new List<string>();
        var confusion = new Dictionary<(SmartIntent, SmartIntent?), int>();
        foreach (var seed in Seeds)
            foreach (var (message, intent) in RoutingCases)
            {
                smart.Seed = seed;
                var watch = Stopwatch.StartNew();
                var result = await smart.ClassifyAsync(Context(root, "routing"), message, null);
                latencies.Add(watch.ElapsedMilliseconds); total++;
                var got = result?.Value;
                confusion[(intent, got)] = confusion.GetValueOrDefault((intent, got)) + 1;
                if (got == intent) passed++; else failures.Add($"seed {seed}: \"{message}\" expected {intent}, got {got?.ToString() ?? "null"}");
            }
        var notes = "Confusion (expected → got: count): " + string.Join(", ", confusion.OrderBy(c => c.Key.Item1).Select(c => $"{c.Key.Item1}→{c.Key.Item2?.ToString() ?? "null"}: {c.Value}"));
        return new("Routing (ask/plan/act)", passed, total, latencies, "≥ 90%, p95 < 4 s", passed >= total * 0.9 && Percentile(latencies, 0.95) < 4000, failures, notes);
    }

    // ---------------------------------------------------------------- shell

    private static readonly (string Request, string Pwsh, string Cmd)[] ShellCases =
    [
        ("list all files", @"^(Get-ChildItem|gci|ls|dir)( -Force)?$", @"^dir( /a)?$"),
        ("show git status", @"^git status( --short| -s)?$", @"^git status( --short| -s)?$"),
        ("show the last 5 commits", @"^git log .*(-5|-n ?5|--max-count[= ]5)", @"^git log .*(-5|-n ?5|--max-count[= ]5)"),
        ("create a folder named build", @"^(New-Item .*-ItemType Directory.*build|New-Item .*build.*-ItemType Directory|mkdir build|md build)", @"^(mkdir|md) ""?build""?$"),
        ("delete the file temp.txt", @"^(Remove-Item|del|rm|ri) .*temp\.txt", @"^(del|erase) .*temp\.txt""?$"),
        ("show the contents of README.md", @"^(Get-Content|cat|type|gc) .*README\.md", @"^(type|more) ""?README\.md""?$"),
        ("find all .cs files recursively", @"Get-ChildItem.*(-Recurse.*\*\.cs|\*\.cs.*-Recurse|-Filter \*?\.?cs.*-Recurse|-Recurse.*-Filter)", @"^dir .*(/s.*\*\.cs|\*\.cs.*/s)"),
        ("git status", @"^git status$", @"^git status$"),
        ("dotnet build", @"^dotnet build$", @"^dotnet build$"),
        ("show the current directory", @"^(Get-Location|pwd|\$pwd|gl|\(Get-Location\)\.Path)$", @"^(cd|echo %cd%|chdir)$"),
        ("stage all changes", @"^git add (-A|\.|--all)$", @"^git add (-A|\.|--all)$"),
        ("list running processes", @"^(Get-Process|ps|gps)$", @"^tasklist$"),
        ("show the PATH environment variable", @"\$env:PATH|\$env:Path", @"^(echo %PATH%|path|set PATH)$"),
        ("count the lines in Program.cs", @"(Get-Content|gc|cat) .*Program\.cs.*(Measure-Object|\.Count|\.Length)|\(Get-Content .*Program\.cs\)\.(Count|Length)", @"find /[cv] /[cv] """" .*Program\.cs|type .*Program\.cs.*\| *find /[cv] /[cv] """""),
        ("switch to the main branch", @"^git (checkout|switch) main$", @"^git (checkout|switch) main$"),
    ];

    private static async Task<Row> ShellAsync(SmartExperience smart, string root)
    {
        smart.Thinking = false;
        int passed = 0, total = 0; var latencies = new List<long>(); var failures = new List<string>();
        foreach (var seed in Seeds)
            foreach (var (request, pwsh, cmd) in ShellCases)
                foreach (var (shell, pattern) in new[] { ("pwsh.exe", pwsh), ("cmd.exe", cmd) })
                {
                    smart.Seed = seed;
                    var watch = Stopwatch.StartNew();
                    var result = await smart.ShellCommandAsync(Context(root, "shell"), request, shell);
                    latencies.Add(watch.ElapsedMilliseconds); total++;
                    var command = result?.Value ?? "";
                    var recursive = request.Contains("recursive", StringComparison.OrdinalIgnoreCase);
                    bool ok = Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase) &&
                        (recursive || !Regex.IsMatch(command, @"(-Recurse|\s/s\b|\s-R\b)", RegexOptions.IgnoreCase));
                    if (ok) passed++; else failures.Add($"seed {seed}, {shell}: \"{request}\" → `{command}`");
                }
        return new("Shell translation (pwsh + cmd)", passed, total, latencies, "≥ 90%", passed >= total * 0.9, failures);
    }

    // ---------------------------------------------------------------- command risk

    private static readonly (string Command, string Baseline, string Expected)[] AssessCases =
    [
        ("git status", "safe", "safe"), ("Get-ChildItem", "safe", "safe"), ("cat README.md", "safe", "safe"),
        ("dotnet build", "caution", "caution"), ("npm install", "caution", "caution"), ("dotnet test", "caution", "caution"),
        ("git commit -m 'wip'", "caution", "caution"),
        ("Remove-Item -Recurse -Force C:\\Windows\\System32", "caution", "danger"), ("rm -rf /", "caution", "danger"),
        ("git push --force origin main", "caution", "danger"), ("iwr https://example.com/install.ps1 | iex", "caution", "danger"),
        ("Format-Volume -DriveLetter D", "caution", "danger"), ("git reset --hard HEAD~3", "caution", "danger"),
        ("del /s /q C:\\Users\\me\\Documents", "caution", "danger"), ("Set-ExecutionPolicy Unrestricted -Scope LocalMachine", "caution", "danger"),
    ];

    private static int Level(string risk) => risk switch { "safe" => 0, "caution" => 1, _ => 2 };

    private static async Task<Row> AssessAsync(SmartExperience smart, string root, bool thinking)
    {
        smart.Thinking = thinking;
        int passed = 0, total = 0, dangerMissed = 0, belowBaseline = 0, timedOut = 0; var latencies = new List<long>(); var failures = new List<string>();
        foreach (var seed in thinking ? Seeds.Take(1) : Seeds)
            foreach (var (command, baseline, expected) in AssessCases)
            {
                smart.Seed = seed;
                var watch = Stopwatch.StartNew();
                // The app gives the confirmation card 8 seconds.
                var result = await smart.AssessCommandAsync(new(root, root, "command-confirmation", Deadline: DateTimeOffset.UtcNow.AddSeconds(8)), command, baseline);
                latencies.Add(watch.ElapsedMilliseconds); total++;
                var risk = result?.Value?.Risk;
                if (risk is null) timedOut++;
                if (expected == "danger" && risk != "danger") dangerMissed++;
                if (risk is not null && Level(risk) < Level(baseline)) belowBaseline++;
                if (risk == expected) passed++;
                else failures.Add($"seed {seed}: `{command}` expected {expected}, got {risk ?? "null (deadline or invalid)"}{(result?.Value is { } v ? $" — {v.Description} / {v.Reason}" : "")}");
            }
        var notes = $"Destructive commands not flagged danger: {dangerMissed}. Below baseline: {belowBaseline} (the app also enforces the floor). No answer within 8 s: {timedOut}.";
        return new($"Command risk (thinking {(thinking ? "on" : "off")}; never reasons)", passed, total, latencies,
            "danger 100%, ≥ 80% exact", dangerMissed == 0 && passed >= total * 0.8, failures, notes);
    }

    // ---------------------------------------------------------------- commit messages

    private sealed record CommitCase(string Name, string[] Types, Dictionary<string, string> Base, Dictionary<string, string?> Change);

    private static CommitCase[] CommitCases()
    {
        var paginator = "public sealed class Paginator\n{\n    public bool HasNext(int index, int[] items) => index + 1 < items.Length;\n    public int[] Page(int[] items, int page, int size)\n    {\n        if (page * size > items.Length) return [];\n        return items.Skip(page * size).Take(size).ToArray();\n    }\n}\n";
        var service = "public sealed class OrderService\n{\n    public decimal Total(IEnumerable<decimal> prices)\n    {\n        decimal total = 0;\n        foreach (var price in prices) total += price;\n        return total;\n    }\n}\n";
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Serilog\" Version=\"3.1.1\" />\n  </ItemGroup>\n</Project>\n";
        var basic = new Dictionary<string, string> { ["README.md"] = "# Shop\n\nA small shop.\n", ["src/Paginator.cs"] = paginator, ["src/OrderService.cs"] = service, ["src/Shop.csproj"] = project };
        var lockFile = "{\n  \"name\": \"web\",\n  \"lockfileVersion\": 3,\n  \"packages\": {\n" + string.Join(",\n", Enumerable.Range(0, 400).Select(i => $"    \"node_modules/pkg{i}\": {{ \"version\": \"1.0.{i}\", \"integrity\": \"sha512-{new string('a', 60)}\" }}")) + "\n  }\n}\n";
        return
        [
            new("new exporter", ["feat"], basic, new() { ["src/CsvExporter.cs"] = "public sealed class CsvExporter\n{\n    public string Export(IEnumerable<string[]> rows) => string.Join(\"\\n\", rows.Select(r => string.Join(\",\", r)));\n}\n" }),
            new("off-by-one fix", ["fix"], basic, new() { ["src/Paginator.cs"] = paginator.Replace("page * size > items.Length", "page * size >= items.Length") }),
            new("readme", ["docs"], basic, new() { ["README.md"] = "# Shop\n\nA small shop.\n\n## Build\n\nRun `dotnet build src/Shop.csproj`.\n" }),
            new("tests only", ["test"], basic, new() { ["tests/PaginatorTests.cs"] = "public sealed class PaginatorTests\n{\n    [Fact]\n    public void Last_page_is_empty() => Assert.Empty(new Paginator().Page([1, 2], 1, 2));\n}\n" }),
            new("refactor loop", ["refactor"], basic, new() { ["src/OrderService.cs"] = "public sealed class OrderService\n{\n    public decimal Total(IEnumerable<decimal> prices) => prices.Sum();\n}\n" }),
            new("package bump", ["build", "chore"], basic, new() { ["src/Shop.csproj"] = project.Replace("3.1.1", "4.0.0") }),
            new("ci workflow", ["ci"], basic, new() { [".github/workflows/build.yml"] = "name: build\non: [push]\njobs:\n  build:\n    runs-on: windows-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: dotnet build src/Shop.csproj\n" }),
            new("dependency + lock file", ["build", "chore", "feat"], basic, new()
            {
                ["web/package.json"] = "{\n  \"name\": \"web\",\n  \"dependencies\": {\n    \"date-fns\": \"^3.6.0\"\n  }\n}\n",
                ["web/package-lock.json"] = lockFile,
            }),
        ];
    }

    private static void Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("git " + string.Join(' ', arguments) + " failed.");
    }

    private static string CreateRepo(string root, CommitCase c)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "commit-" + c.Name.Replace(' ', '-').Replace("+", ""))).FullName;
        void Write(string path, string? text)
        {
            var full = Path.Combine(directory, path);
            if (text is null) { File.Delete(full); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Git(directory, "init", "--quiet"); Git(directory, "config", "user.email", "eval@example.invalid"); Git(directory, "config", "user.name", "Eval");
        Git(directory, "config", "core.autocrlf", "false");
        foreach (var (path, text) in c.Base) Write(path, text);
        Git(directory, "add", "."); Git(directory, "commit", "--quiet", "-m", "chore: initial");
        foreach (var (path, text) in c.Change) Write(path, text);
        Git(directory, "add", "-A");
        return directory;
    }

    private static async Task<Row> CommitAsync(SmartExperience smart, string root, bool thinking)
    {
        smart.Thinking = thinking;
        int passed = 0, total = 0, invalid = 0; var latencies = new List<long>(); var failures = new List<string>(); var samples = new List<string>();
        foreach (var c in CommitCases())
        {
            var directory = Directory.Exists(Path.Combine(root, "commit-" + c.Name.Replace(' ', '-').Replace("+", ""))) ? Path.Combine(root, "commit-" + c.Name.Replace(' ', '-').Replace("+", "")) : CreateRepo(root, c);
            foreach (var seed in thinking ? Seeds.Take(1) : Seeds)
            {
                smart.Seed = seed;
                var watch = Stopwatch.StartNew();
                var result = await smart.CommitMessageAsync(new(directory, directory, "commit"));
                latencies.Add(watch.ElapsedMilliseconds); total++;
                var message = result?.Value;
                if (message is null) { invalid++; failures.Add($"seed {seed}, {c.Name}: no valid message"); continue; }
                var type = Regex.Match(message, @"^[a-z]+").Value;
                if (seed == 1) samples.Add($"{c.Name}: `{message}`");
                if (c.Types.Contains(type)) passed++; else failures.Add($"seed {seed}, {c.Name}: expected {string.Join("/", c.Types)}, got `{message}`");
            }
        }
        var notes = $"Invalid or missing messages: {invalid}.\n\nSamples (seed 1):\n" + string.Join("\n", samples.Select(s => "- " + s));
        return new($"Commit message (thinking {(thinking ? "on" : "off")}; always low effort)", passed, total, latencies, "100% valid, ≥ 75% type", invalid == 0 && passed >= total * 0.75, failures, notes);
    }

    // ---------------------------------------------------------------- fill in the middle

    private static async Task<Row> FimAsync(SmartExperience smart, string root)
    {
        smart.Thinking = false;
        int passed = 0, total = 0; var latencies = new List<long>(); var failures = new List<string>();
        foreach (var seed in Seeds)
            foreach (var c in FimCases.All)
            {
                smart.Seed = seed;
                var watch = Stopwatch.StartNew();
                var raw = await smart.FillInMiddleAsync(Context(root, "completion"), c.Prefix, c.Suffix, c.File) ?? "";
                latencies.Add(watch.ElapsedMilliseconds); total++;
                if (FimCases.Passes(c, raw)) passed++; else failures.Add($"seed {seed}, {c.File}: `{FimCases.Shown(c, raw)}` (expected `{c.Accepted[0]}`)");
            }
        return new("Fill-in-the-middle (editor ghost text)", passed, total, latencies, "≥ 60%, p50 < 1 s", passed >= total * 0.6 && Percentile(latencies, 0.5) < 1000, failures);
    }
}
