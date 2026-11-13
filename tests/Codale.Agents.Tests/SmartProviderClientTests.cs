using System.Net;
using System.Text;
using System.Text.Json;
using Codale.App.Services;
using Codale.Core.Features;

namespace Codale.Agents.Tests;

public sealed class SmartProviderClientTests
{
    private const string General = SmartFeatureSettings.General;
    private const string Completion = SmartFeatureSettings.Completion;
    private const string Embedding = SmartFeatureSettings.Embedding;
    private const string Decision = SmartFeatureSettings.Decision;

    [Fact]
    public async Task OpenRouter_code_models_use_chat_insertion_and_preserve_whitespace()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("qwen/code-instruct", body.RootElement.GetProperty("model").GetString());
            Assert.Contains("suffix", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"\\n    return a + b;\"}}]}");
        }));
        var profile = Profile(); profile.BaseUrl = "https://openrouter.ai/api/v1";
        var client = new SmartProviderClient(Completion, "openai", profile, "qwen/code-instruct", http);
        Assert.Equal("\n    return a + b;", await client.CompleteAsync("completion", "", "{\"prefix\":\"\",\"suffix\":\"}\"}", 48, default));
    }

    [Fact]
    public async Task Code_completion_uses_fim_prefix_and_suffix_and_preserves_inserted_whitespace()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://example.test/v1/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("fim-code-model", root.GetProperty("model").GetString());
            Assert.Equal("function test() {", root.GetProperty("prompt").GetString());
            Assert.Equal("\n}", root.GetProperty("suffix").GetString());
            Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
            Assert.False(root.TryGetProperty("messages", out _));
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { text = "\n    return 1;" } } }));
        }));
        var client = new SmartProviderClient(Completion, "openai", Profile(), "fim-code-model", http);
        var input = JsonSerializer.Serialize(new { prefix = "function test() {", suffix = "\n}", fileName = "test.js" });
        Assert.Equal("\n    return 1;", await client.CompleteAsync("completion", "ignored chat instructions", input, 128, default));
    }

    [Fact]
    public async Task Composer_and_shell_completion_use_fim_model_when_set()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.EndsWith("/completions", request.RequestUri!.AbsolutePath);
            Assert.DoesNotContain("chat", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("> git st", body.RootElement.GetProperty("prompt").GetString());
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { text = "atus --short" } } }));
        }));
        var router = Router(http, completion: new(Completion, "openai", Profile(), "fim-model", http));
        var input = JsonSerializer.Serialize(new { prefix = "> git st", suffix = "" });
        Assert.Equal("atus --short", await router.CompleteAsync("shell-completion", "", input, 32, default));
    }

    [Fact]
    public async Task Composer_completion_on_an_openrouter_code_model_continues_the_prompt_not_code()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Contains("developer's unfinished message", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() >= 256);
            return Json("{\"choices\":[{\"message\":{\"content\":\"validates the email\"}}]}");
        }));
        var profile = Profile(); profile.BaseUrl = "https://openrouter.ai/api/v1";
        var router = Router(http, general: new(General, "openai", Profile(), "general-model", http),
            completion: new(Completion, "openai", profile, "qwen/coder", http));
        Assert.Equal("validates the email", await router.CompleteAsync("compose-completion", "", "{\"prefix\":\"fix login so it \",\"suffix\":\"\"}", 32, default));
        // Ghost text needs a FIM model: General is too slow to stand in.
        Assert.Null(Router(http, general: new(General, "openai", Profile(), "general-model", http)).ClientFor("compose-completion"));
        Assert.Null(Router(http, general: new(General, "openai", Profile(), "general-model", http)).ClientFor("shell-completion"));
    }

    [Fact]
    public async Task Decision_model_answers_through_the_decisions_api()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://openrouter.ai/api/alpha/decisions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("perplexity/decider", body.RootElement.GetProperty("model").GetString());
            var intent = body.RootElement.GetProperty("questions").GetProperty("intent");
            Assert.Equal("choice", intent.GetProperty("type").GetString());
            Assert.True(intent.GetProperty("criteria").TryGetProperty("plan", out _));
            Assert.Equal("score", body.RootElement.GetProperty("questions").GetProperty("risk").GetProperty("type").GetString());
            return Json("{\"answers\":{\"intent\":{\"type\":\"choice\",\"choice\":\"ask\",\"probabilities\":{\"ask\":0.84,\"plan\":0.09,\"act\":0.07},\"confidence\":0.76}," +
                "\"risk\":{\"type\":\"score\",\"score\":1.94,\"probabilities\":{\"0\":0.01,\"1\":0.05,\"2\":0.94}},\"other\":{\"type\":\"refusal\"}}}");
        }));
        var profile = Profile(); profile.BaseUrl = "https://openrouter.ai/api/v1";
        var decider = new SmartProviderClient(Decision, "openai", profile, "perplexity/decider", http);
        var answers = await decider.DecideAsync(new { message = "show me how the router works" }, new Dictionary<string, SmartQuestion>
        {
            ["intent"] = SmartDecisions.IntentQuestion(), ["risk"] = SmartDecisions.RiskQuestion(),
        }, default);
        Assert.Equal("ask", answers["intent"].Choice);
        Assert.Equal(0.84, answers["intent"].Certainty, 2);
        Assert.Equal("danger", SmartDecisions.ToRisk(answers["risk"]));
        Assert.False(answers.ContainsKey("other"));
        // A decision model never writes text.
        await Assert.ThrowsAsync<InvalidOperationException>(() => decider.CompleteAsync("routing", "s", "p", 64, default));
    }

    [Fact]
    public void Redirects_need_a_sure_answer_naming_a_listed_session()
    {
        FeatureInfo F(string id) => new() { Id = id, Name = id };
        var sessions = new List<FeatureInfo> { F("settings"), F("login") };
        SmartDecision Pick(string choice, double p) => new("choice", choice, null, null, new Dictionary<string, double> { [choice] = p }, p);
        Assert.Equal(("login", false), SmartDecisions.Redirect(Pick("login", 0.8), sessions, "settings"));
        Assert.Equal(((string?)null, true), SmartDecisions.Redirect(Pick("new", 0.7), sessions, "settings"));
        Assert.Null(SmartDecisions.Redirect(Pick("login", 0.4), sessions, "settings"));
        Assert.Null(SmartDecisions.Redirect(Pick("current", 0.99), sessions, "settings"));
        Assert.Null(SmartDecisions.Redirect(Pick("settings", 0.99), sessions, "settings"));
        Assert.Null(SmartDecisions.Redirect(Pick("unknown", 0.99), sessions, "settings"));
        Assert.Null(SmartDecisions.Redirect(null, sessions, "settings"));
        Assert.DoesNotContain("settings", ((IReadOnlyDictionary<string, string>)SmartDecisions.SessionQuestion(sessions, "settings").Criteria!).Keys);
    }

    [Theory]
    [InlineData("claude", "fim-model")]
    [InlineData("codex", "fim-model")]
    [InlineData("anthropic", "fim-model")]
    [InlineData("openai", "")]
    [InlineData("openai", "default")]
    public async Task Specialized_roles_require_compatible_provider_and_explicit_model(string provider, string model)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No HTTP request expected")));
        var completion = new SmartProviderClient(Completion, provider, Profile(), model, http);
        var embedding = new SmartProviderClient(Embedding, provider, Profile(), model, http);
        Assert.False(completion.IsEnabled);
        Assert.False(embedding.IsEnabled);
        Assert.Equal("off", embedding.EmbeddingIdentity);
        Assert.Empty(await embedding.EmbedAsync("code", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.CompleteAsync("completion", "", "{}", 64, default));
    }

    [Fact]
    public void Specialized_models_do_not_inherit_flash_but_text_roles_do()
    {
        Assert.Equal("", new SmartProviderClient(Completion, "openai", Profile(), "").Model);
        Assert.Equal("", new SmartProviderClient(Embedding, "openai", Profile(), "").Model);
        Assert.Equal("default-model", new SmartProviderClient(General, "openai", Profile(), "").Model);
        Assert.Equal("", new SmartProviderClient(Decision, "openai", Profile(), "default").Model);
    }

    [Theory]
    [InlineData("claude", "", "account-flash")]
    [InlineData("codex", "default", "account-flash")]
    [InlineData("claude", "Default", "account-flash")]
    [InlineData("codex", "specific-model", "specific-model")]
    [InlineData("openai", "", "account-flash")]
    public void Smart_model_inherits_flash_unless_explicitly_selected(string provider, string selected, string expected)
    {
        var profile = new ByokProvider { IsAccount = provider is "claude" or "codex", FlashModel = "account-flash" };
        var client = new SmartProviderClient(General, provider, profile, selected);
        Assert.Equal(expected, client.Model);
        using var identity = JsonDocument.Parse(client.FeatureIdentity);
        Assert.Equal(expected, identity.RootElement.GetProperty("Model").GetString());
    }

    [Fact]
    public void Legacy_default_resolves_the_only_detected_account_instead_of_an_empty_home()
    {
        var account = new ByokProvider { Id = "detected", CliId = "claude", IsAccount = true, AccountId = "user", Email = "dev@example.test" };
        var resolved = SmartFeatureSettings.ResolveAccount([account], "claude", "", null);
        Assert.Same(account, resolved);
        Assert.Equal("user", new SmartProviderClient(General, "claude", resolved, "").AccountId);
    }

    [Fact]
    public void Explicit_account_is_not_folded_into_default_even_when_email_matches()
    {
        var primary = new ByokProvider { Id = "primary", CliId = "claude", IsAccount = true, AccountId = "", Email = "dev@example.test" };
        var detected = new ByokProvider { Id = "detected", CliId = "claude", IsAccount = true, AccountId = "user", Email = primary.Email };
        Assert.Same(detected, SmartFeatureSettings.ResolveAccount([primary, detected], "claude", "detected", primary.Email));
        Assert.Same(primary, SmartFeatureSettings.ResolveAccount([primary, detected], "claude", "", primary.Email));
    }

    [Fact]
    public void Legacy_default_email_selects_matching_account_for_the_correct_cli()
    {
        var claude = new ByokProvider { Id = "claude", CliId = "claude", IsAccount = true, AccountId = "user", Email = "dev@example.test" };
        var codex = new ByokProvider { Id = "codex", CliId = "codex", IsAccount = true, AccountId = "user", Email = claude.Email };
        var other = new ByokProvider { Id = "other", CliId = "claude", IsAccount = true, AccountId = "other", Email = "other@example.test" };
        Assert.Same(claude, SmartFeatureSettings.ResolveAccount([codex, other, claude], "claude", "", claude.Email));
        Assert.Null(SmartFeatureSettings.ResolveAccount([codex, other, claude], "claude", "missing", claude.Email));
        Assert.Null(SmartFeatureSettings.ResolveAccount([other, claude], "claude", "", null));
    }

    [Theory]
    [InlineData("claude", "0123456789abcdef0123456789abcdef")]
    [InlineData("codex", "fedcba9876543210fedcba9876543210")]
    [InlineData("claude", "")]
    [InlineData("codex", "")]
    public void Cli_requests_preserve_selected_account(string provider, string accountId)
    {
        var profile = new ByokProvider { IsAccount = true, CliId = provider, AccountId = accountId };
        var client = new SmartProviderClient(General, provider, profile, "");
        Assert.Equal(accountId, client.AccountId);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Default_cli_requests_use_codale_default_account(string provider)
    {
        var client = new SmartProviderClient(General, provider, null, "");
        Assert.Null(client.AccountId);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Cli_launch_uses_selected_user_account_and_model(string provider)
    {
        var profile = new ByokProvider { IsAccount = true, CliId = provider, AccountId = Codale.Core.Agents.CodaleAccount.UserAccountId };
        var client = new SmartProviderClient(General, provider, profile, "");
        var start = client.BuildCliStartInfo("agent.exe", ".", "answer.txt", "selected-model", "instructions");
        var variable = provider == "codex" ? Codale.Core.Agents.CodaleAccount.CodexVariable : Codale.Core.Agents.CodaleAccount.ClaudeVariable;
        Assert.Equal(Codale.Core.Agents.CodaleAccount.ProfileHome(provider, profile.AccountId), start.Environment[variable]);
        var args = start.ArgumentList.ToList();
        Assert.Equal("selected-model", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task Roles_route_independently_and_off_does_not_send_requests()
    {
        int openai = 0, anthropic = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
            {
                anthropic++;
                return Task.FromResult(Json("{\"content\":[{\"type\":\"text\",\"text\":\"general\"}]}"));
            }
            openai++;
            return Task.FromResult(Json("{\"choices\":[{\"text\":\"code\"}]}"));
        }));
        var router = Router(http, general: new(General, "anthropic", Profile(), "model", http),
            completion: new(Completion, "openai", Profile(), "fim", http));
        Assert.True(router.HasGeneration);
        Assert.True(router.IsFeatureEnabled("commit"));
        Assert.True(router.IsFeatureEnabled("summary"));
        Assert.False(router.IsFeatureEnabled("embedding"));
        Assert.Equal("general", await router.CompleteAsync("commit", "s", "p", 64, default));
        Assert.Equal("general", await router.CompleteAsync("shell", "s", "p", 64, default));
        Assert.Equal("code", await router.CompleteAsync("completion", "s", "{\"prefix\":\"a\",\"suffix\":\"\"}", 64, default));
        Assert.Equal(1, openai);
        Assert.Equal(2, anthropic);
    }

    [Theory]
    [InlineData("routing", true)]
    [InlineData("assess", true)]
    [InlineData("command-confirmation", true)]
    [InlineData("shell", false)]
    [InlineData("keywords", false)]
    [InlineData("commit", false)]
    public void Decision_answers_classification_and_general_writes_text(string task, bool decides)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No HTTP request expected")));
        var general = new SmartProviderClient(General, "openai", Profile(), "general-model", http);
        var withDecider = Router(http, general: general, decision: new(Decision, "openai", Profile(), "decider", http));
        Assert.Equal(decides, withDecider.Decides(task));
        Assert.Equal(General, withDecider.ClientFor(task)?.Role);
        Assert.False(Router(http, general: general).Decides(task));
        // A decision model needs an API endpoint and an explicit model: no Flash fallback, no CLI.
        Assert.Null(Router(http, decision: new(Decision, "openai", Profile(), "", http)).Decider);
        Assert.Null(Router(http, decision: new(Decision, "claude", Profile(), "decider", http)).Decider);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("summary")]
    [InlineData("context")]
    [InlineData("title")]
    public void General_tasks_never_run_on_decision(string task)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No HTTP request expected")));
        var decision = new SmartProviderClient(Decision, "openai", Profile(), "fast", http);
        Assert.Null(Router(http, decision: decision).ClientFor(task));
        Assert.Equal(General, Router(http, general: new(General, "openai", Profile(), "", http), decision: decision).ClientFor(task)?.Role);
    }

    private static SmartFeatureRouter Router(HttpClient http, SmartProviderClient? general = null, SmartProviderClient? completion = null,
        SmartProviderClient? embedding = null, SmartProviderClient? decision = null) => new(new Dictionary<string, SmartProviderClient>
    {
        [General] = general ?? new(General, "off", Profile(), "", http),
        [Completion] = completion ?? new(Completion, "off", Profile(), "", http),
        [Embedding] = embedding ?? new(Embedding, "off", Profile(), "", http),
        [Decision] = decision ?? new(Decision, "off", Profile(), "", http),
    });

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static ByokProvider Profile() => new() { Id = "profile", Name = "Test", BaseUrl = "https://example.test/v1", AnthropicBaseUrl = "https://example.test/v1", ApiKey = "test-key", FlashModel = "default-model" };

    [Theory]
    [InlineData("openai", "chat/completions")]
    [InlineData("anthropic", "messages")]
    public async Task Generation_uses_selected_role_model_endpoint_and_protocol(string provider, string suffix)
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://example.test/v1/" + suffix, request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("commit-model", body.RootElement.GetProperty("model").GetString());
            if (provider == "openai")
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("test-key", request.Headers.Authorization.Parameter);
                Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
                return Json("{\"choices\":[{\"message\":{\"content\":\"feat: add setting\"}}]}");
            }
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Equal("system", body.RootElement.GetProperty("system").GetString());
            return Json("{\"content\":[{\"type\":\"text\",\"text\":\"feat: add setting\"}]}");
        }));
        var client = new SmartProviderClient(General, provider, Profile(), "commit-model", http);
        Assert.Equal("feat: add setting", await client.CompleteAsync("commit", "system", "diff", 64, default));
    }

    [Fact]
    public async Task Blank_model_uses_profile_default_and_off_role_never_sends()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("default-model", body.RootElement.GetProperty("model").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        }));
        Assert.Equal("ok", await new SmartProviderClient(General, "openai", Profile(), "", http).CompleteAsync("commit", "system", "input", 64, default));
        var off = new SmartProviderClient(General, "openai", Profile(), "off", http);
        Assert.False(off.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.CompleteAsync("commit", "system", "input", 64, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Embedding_model_is_explicit_normalized_and_separated_by_endpoint()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://example.test/v1/embeddings", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("embedding-model", body.RootElement.GetProperty("model").GetString());
            return Json("{\"data\":[{\"embedding\":[3,4]}]}");
        }));
        var client = new SmartProviderClient(Embedding, "openai", Profile(), "embedding-model", http);
        Assert.Equal(new[] { 0.6f, 0.8f }, await client.EmbedAsync("code", default));
        var other = Profile(); other.BaseUrl = "https://other.test/v1";
        Assert.NotEqual(client.EmbeddingIdentity, new SmartProviderClient(Embedding, "openai", other, "embedding-model").EmbeddingIdentity);
        // Only the Embedding role embeds, and only on an OpenAI-compatible endpoint.
        Assert.Equal("off", new SmartProviderClient(General, "openai", Profile(), "embedding-model", http).EmbeddingIdentity);
        foreach (var provider in new[] { "off", "claude", "codex", "anthropic" })
        {
            var unsupported = new SmartProviderClient(Embedding, provider, Profile(), "embedding-model", http);
            Assert.Equal("off", unsupported.EmbeddingIdentity);
            Assert.Empty(await unsupported.EmbedAsync("code", default));
        }
    }

    [Fact]
    public async Task Http_errors_do_not_expose_credentials_or_server_body()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("test-key: secret server detail") })));
        var client = new SmartProviderClient(General, "openai", Profile(), "", http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("commit", "s", "p", 64, default));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain("secret server detail", error.Message);
    }

    [Fact]
    public void Legacy_groups_migrate_developer_tools_to_general_once()
    {
        var values = new Dictionary<string, string>
        {
            ["ai.smart.groups.assistance.provider"] = "openai",
            ["ai.smart.groups.assistance.openai.profile"] = "router",
            ["ai.smart.openai.groups.assistance.model"] = "flash-model",
            ["ai.smart.groups.context.provider"] = "claude",
            ["ai.smart.claude.groups.context.model"] = "context-model",
            ["ai.smart.groups.completion.provider"] = "openai",
            ["ai.smart.openai.groups.completion.model"] = "fim-model",
            ["ai.smart.groups.embedding.provider"] = "off",
            ["ai.smartThinking"] = "0",
        };
        Assert.True(SmartFeatureSettings.MigrateLegacyGroups(values));
        Assert.Equal("openai", values["ai.smart.groups.general.provider"]);
        Assert.Equal("router", values["ai.smart.groups.general.openai.profile"]);
        Assert.Equal("flash-model", values["ai.smart.openai.groups.general.model"]);
        Assert.Equal("fim-model", values["ai.smart.openai.groups.completion.model"]);
        Assert.Equal("off", values["ai.smart.groups.decision.provider"]);
        Assert.DoesNotContain(values.Keys, k => k.Contains(".assistance.") || k.Contains(".context.") || k == "ai.smartThinking");

        values["ai.smart.groups.general.provider"] = "codex";
        Assert.False(SmartFeatureSettings.MigrateLegacyGroups(values));
        Assert.Equal("codex", values["ai.smart.groups.general.provider"]);
    }

    [Fact]
    public void Legacy_codebase_context_becomes_general_when_developer_tools_was_off()
    {
        var values = new Dictionary<string, string>
        {
            ["ai.smart.groups.assistance.provider"] = "off",
            ["ai.smart.groups.context.provider"] = "anthropic",
            ["ai.smart.groups.context.anthropic.profile"] = "direct",
            ["ai.smart.anthropic.groups.context.model"] = "haiku",
        };
        Assert.True(SmartFeatureSettings.MigrateLegacyGroups(values));
        Assert.Equal("anthropic", values["ai.smart.groups.general.provider"]);
        Assert.Equal("direct", values["ai.smart.groups.general.anthropic.profile"]);
        Assert.Equal("haiku", values["ai.smart.anthropic.groups.general.model"]);
    }
}
