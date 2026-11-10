using System.Text.Json;

namespace Codale.Smart.Tests;

public sealed class CommandAssessmentTests
{
    private sealed class Runtime(string output) : ISmartRuntime
    {
        public List<string> Prompts { get; } = [];
        public string? Grammar;
        public string Backend => "test";
        public void Load(string path, bool gpu, CancellationToken ct) { }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null)
        { ct.ThrowIfCancellationRequested(); Prompts.Add(prompt); Grammar = grammar; return output; }
        public void Dispose() { }
    }

    [Fact]
    public async Task Assessment_describes_command_without_tools_or_execution()
    {
        var runtime = new Runtime("""{"description":"Delete build output","risk":"caution","reason":"Removes workspace files"}""");
        await using var smart = new SmartExperience(() => runtime, (_, _) => Task.FromResult("model"));
        await smart.SetEnabledAsync(true);
        var folder = Environment.CurrentDirectory;
        var result = await smart.AssessCommandAsync(new(folder, folder, "command-confirmation"),
            "Remove-Item -Recurse bin", "Caution - deletes workspace files");
        Assert.Equal("Delete build output", result?.Value?.Description);
        Assert.Equal("caution", result?.Value?.Risk);
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("Remove-Item -Recurse bin", prompt);
        Assert.Contains("Never lower the baseline risk", prompt);
        Assert.DoesNotContain("<tools>", prompt);
        Assert.Contains("risk", runtime.Grammar!);
        Assert.Empty(result!.Evidence);
        Assert.Empty(Assert.Single(smart.Activities(folder)).Tools!);
    }

    [Theory]
    [InlineData("{}")] 
    [InlineData("not JSON")]
    [InlineData("{\"description\":\"Delete files\",\"risk\":\"low\",\"reason\":\"workspace\"}")]
    [InlineData("{\"description\":\"\",\"risk\":\"safe\",\"reason\":\"workspace\"}")]
    [InlineData("{\"description\":42,\"risk\":\"safe\",\"reason\":\"workspace\"}")]
    [InlineData("{\"description\":\"Run\\ncommands\",\"risk\":\"safe\",\"reason\":\"workspace\"}")]
    [InlineData("{\"description\":\"Run commands\",\"risk\":\"safe\",\"reason\":\"<tool_call>\"}")]
    public void Invalid_assessments_leave_the_baseline_available(string text) =>
        Assert.Null(SmartExperience.ParseCommandAssessment(text));

    [Fact]
    public void Oversized_or_empty_explanations_are_rejected()
    {
        Assert.Null(SmartExperience.ParseCommandAssessment(JsonSerializer.Serialize(new
            { description = new string('x', 141), risk = "safe", reason = "Reads files" })));
        Assert.Null(SmartExperience.ParseCommandAssessment(JsonSerializer.Serialize(new
            { description = "Read files", risk = "safe", reason = " " })));
    }

    [Fact]
    public async Task Disabled_and_expired_requests_skip_inference()
    {
        var runtime = new Runtime("{}");
        await using var smart = new SmartExperience(() => runtime, (_, _) => Task.FromResult("model"));
        var folder = Environment.CurrentDirectory;
        Assert.Null(await smart.AssessCommandAsync(new(folder, folder, "confirmation"), "ls", "Safe"));
        await smart.SetEnabledAsync(true);
        Assert.Null(await smart.AssessCommandAsync(new(folder, folder, "confirmation", Deadline: DateTimeOffset.UtcNow.AddSeconds(-1)), "ls", "Safe"));
        Assert.Empty(runtime.Prompts);
    }
}
