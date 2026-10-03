using Codale.Core.Helper;
using Codale.Core.Syntax;

namespace Codale.Core.Tests.Syntax;

public sealed class SyntaxGeneratorTests
{
    private sealed class FakeHelper(params string[] replies) : IHelperModel
    {
        private int _next;

        public List<string> Prompts { get; } = [];

        public bool IsAvailable => true;

        public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }

        public Task<ToolCall?> CallToolAsync(string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private const string Good = """{ "name": "Foo", "scopeName": "source.foo", "fileTypes": ["foo"], "patterns": [ { "match": "\\b(let|fn)\\b", "name": "keyword.control.foo" } ] }""";

    [Fact]
    public async Task A_valid_reply_becomes_a_grammar()
    {
        var generator = new SyntaxGenerator(new FakeHelper(Good));

        var result = await generator.GenerateAsync("Foo", ".foo", "let x = 1");

        Assert.Equal("Foo", result.Name);
        Assert.Equal("source.foo", result.Meta.ScopeName);
        Assert.Equal(["foo"], result.Extensions);
    }

    [Fact]
    public async Task Fences_and_prose_around_the_json_are_ignored()
    {
        var generator = new SyntaxGenerator(new FakeHelper("Here you go:\n```json\n" + Good + "\n```\nEnjoy!"));

        var result = await generator.GenerateAsync("Foo", "foo", "let x = 1");

        Assert.Equal("source.foo", result.Meta.ScopeName);
    }

    [Fact]
    public async Task A_bad_first_reply_is_retried_once_with_the_error()
    {
        var helper = new FakeHelper("""{ "patterns": [] }""", Good);

        var result = await new SyntaxGenerator(helper).GenerateAsync("Foo", "foo", "let x = 1");

        Assert.Equal("source.foo", result.Meta.ScopeName);
        Assert.Equal(2, helper.Prompts.Count);
        Assert.Contains("scopeName", helper.Prompts[1]);
    }

    [Fact]
    public async Task Two_bad_replies_fail_with_a_readable_reason()
    {
        var helper = new FakeHelper("no json at all");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SyntaxGenerator(helper).GenerateAsync("Foo", "foo", "x"));

        Assert.Contains("no JSON", error.Message);
        Assert.Equal(2, helper.Prompts.Count);
    }

    [Fact]
    public async Task A_long_sample_is_trimmed_before_it_is_sent()
    {
        var helper = new FakeHelper(Good);

        await new SyntaxGenerator(helper).GenerateAsync("Foo", "foo", new string('x', 50_000));

        Assert.True(helper.Prompts[0].Length < 8_000);
    }

    [Fact]
    public async Task Cancellation_stops_before_asking_the_model()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var helper = new FakeHelper(Good);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SyntaxGenerator(helper).GenerateAsync("Foo", "foo", "x", ct: cts.Token));

        Assert.Empty(helper.Prompts);
    }
}
