using System.Text;

namespace Codale.Smart.Tests;

/// <summary>
/// SmartConversation must render exactly what Granite 4.2's own chat template renders. The goldens in
/// tests/fixtures/granite were produced by the template embedded in the pinned GGUF (transformers' Jinja settings);
/// regenerate them with the render script described in tests/fixtures/granite/README.md when the model changes.
/// </summary>
public sealed class GraniteTemplateTests
{
    private static readonly object[] Tools =
    [
        new { name = "read_file", description = "Read a file.",
            parameters = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" }, additionalProperties = false } }
    ];
    private const string Call = "<tool_call>\n<function=read_file>\n<parameter=path>\na.txt\n</parameter>\n</function>\n</tool_call>";

    private sealed class Runtime : ISmartRuntime
    {
        public string Backend => "test";
        public void Load(string modelPath, bool gpu, CancellationToken ct) { }
        public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null) => "";
        public void Dispose() { }
    }

    private static string Golden(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tests", "fixtures", "granite", name + ".txt");
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
        }
        throw new FileNotFoundException(name);
    }

    [Theory]
    [InlineData("plain_off", SmartReasoning.Off)]
    [InlineData("plain_full", SmartReasoning.Full)]
    [InlineData("plain_low", SmartReasoning.Low)]
    public void Plain_prompts_match_the_template(string golden, SmartReasoning reasoning) =>
        Assert.Equal(Golden(golden), new SmartConversation("S", "Hello", reasoning).Render(new Runtime(), 0));

    [Theory]
    [InlineData("tools_round_full", SmartReasoning.Full, "I should read it.\n</think>\n\n")]
    [InlineData("tools_round_low", SmartReasoning.Low, "I should read it.\n</think>\n\n")]
    [InlineData("tools_round_off", SmartReasoning.Off, "")]
    public void A_tool_round_matches_the_template(string golden, SmartReasoning reasoning, string thought)
    {
        var conversation = new SmartConversation("S", "Read a.txt", reasoning, Tools);
        conversation.Add(thought + Call, ["{\"text\":\"hi\"}"]);
        Assert.Equal(Golden(golden), conversation.Render(new Runtime(), 0));
    }

    [Fact]
    public void The_final_turn_truncates_earlier_reasoning_like_the_template()
    {
        var conversation = new SmartConversation("S", "Read a.txt", SmartReasoning.Full, Tools);
        conversation.Add("I should read it.\n</think>\n\n" + Call, ["{\"text\":\"hi\"}"]);
        conversation.Add("It says hi.\n</think>\n\nThe file says hi.", []);
        Assert.Equal(Golden("tools_final_truncated"), conversation.Render(new Runtime(), 0, final: true));
    }

    [Fact]
    public void Tool_json_uses_python_separators_and_keeps_non_ascii()
    {
        Assert.Equal("{\"name\": \"x\", \"description\": \"Café \\\"q\\\"\\n\", \"parameters\": {\"type\": \"object\", \"required\": []}}",
            SmartConversation.ToolJson(new { type = "function", function = new { name = "x", description = "Café \"q\"\n", parameters = new { type = "object", required = Array.Empty<string>() }, strict = true } }));
    }

    [Fact]
    public void Calls_quoted_in_reasoning_stay_in_the_reasoning()
    {
        var conversation = new SmartConversation("S", "Read a.txt", SmartReasoning.Full, Tools);
        conversation.Add("Maybe " + Call.Replace("a.txt", "b.txt") + " no.\n</think>\n\n" + Call, ["{}"]);
        var prompt = conversation.Render(new Runtime(), 0);
        Assert.Contains("<think>\nMaybe <tool_call>\n<function=read_file>\n<parameter=path>\nb.txt", prompt);
        Assert.Contains("</think>\n" + Call + "\n<|im_end|>", prompt);
    }
}
