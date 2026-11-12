using System.Net;
using System.Text;
using System.Text.Json;
using Codale.App.Services;

namespace Codale.Agents.Tests;

public sealed class SmartProviderClientTests
{
    [Theory]
    [InlineData("claude", "", "account-flash")]
    [InlineData("codex", "default", "account-flash")]
    [InlineData("claude", "Default", "account-flash")]
    [InlineData("codex", "specific-model", "specific-model")]
    [InlineData("openai", "", "account-flash")]
    public void Smart_model_inherits_flash_unless_explicitly_selected(string provider, string selected, string expected)
    {
        var profile = new ByokProvider { IsAccount = provider is "claude" or "codex", FlashModel = "account-flash" };
        var client = new SmartProviderClient(provider, profile, new Dictionary<string, string> { ["commit"] = selected });
        Assert.Equal(expected, client.ModelFor("commit"));
        using var identity = JsonDocument.Parse(client.FeatureIdentity("commit"));
        Assert.Equal(expected, identity.RootElement.GetProperty("Model").GetString());
        Assert.Equal("account-flash", client.ModelFor("summary"));
    }

    [Fact]
    public void Legacy_default_resolves_the_only_detected_account_instead_of_an_empty_home()
    {
        var account = new ByokProvider { Id = "detected", CliId = "claude", IsAccount = true, AccountId = "user", Email = "dev@example.test" };
        var resolved = SmartFeatureSettings.ResolveAccount([account], "claude", "", null);
        Assert.Same(account, resolved);
        Assert.Equal("user", new SmartProviderClient("claude", resolved, new Dictionary<string, string>()).AccountId);
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
        var client = new SmartProviderClient(provider, profile, new Dictionary<string, string>());
        Assert.Equal(accountId, client.AccountId);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Default_cli_requests_use_codale_default_account(string provider)
    {
        var client = new SmartProviderClient(provider, null, new Dictionary<string, string>());
        Assert.Null(client.AccountId);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Cli_launch_uses_selected_user_account_and_model(string provider)
    {
        var profile = new ByokProvider { IsAccount = true, CliId = provider, AccountId = Codale.Core.Agents.CodaleAccount.UserAccountId };
        var client = new SmartProviderClient(provider, profile, new Dictionary<string, string>());
        var start = client.BuildCliStartInfo("agent.exe", ".", "answer.txt", "selected-model", "instructions");
        var variable = provider == "codex" ? Codale.Core.Agents.CodaleAccount.CodexVariable : Codale.Core.Agents.CodaleAccount.ClaudeVariable;
        Assert.Equal(Codale.Core.Agents.CodaleAccount.ProfileHome(provider, profile.AccountId), start.Environment[variable]);
        var args = start.ArgumentList.ToList();
        Assert.Equal("selected-model", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task Features_route_independently_and_off_does_not_send_requests()
    {
        int openai = 0, anthropic = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
            {
                anthropic++;
                return Task.FromResult(Json("{\"content\":[{\"type\":\"text\",\"text\":\"context\"}]}"));
            }
            openai++;
            return Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"assistance\"}}]}"));
        }));
        var models = SmartFeatureSettings.Features.ToDictionary(f => f.Id, _ => "model");
        var router = new SmartFeatureRouter(new Dictionary<string, SmartProviderClient>
        {
            ["assistance"] = new("openai", Profile(), models, http),
            ["context"] = new("anthropic", Profile(), models, http),
            ["completion"] = new("off", Profile(), models, http),
            ["embedding"] = new("off", Profile(), models, http),
        });
        Assert.True(router.HasGeneration);
        Assert.True(router.IsFeatureEnabled("commit"));
        Assert.True(router.IsFeatureEnabled("summary"));
        Assert.False(router.IsFeatureEnabled("completion"));
        Assert.False(router.IsFeatureEnabled("embedding"));
        Assert.Equal("assistance", await router.CompleteAsync("commit", "s", "p", 64, default));
        Assert.Equal("context", await router.CompleteAsync("summary", "s", "p", 64, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync("completion", "s", "p", 64, default));
        Assert.Equal(1, openai);
        Assert.Equal(1, anthropic);
    }

    [Fact]
    public async Task Grouped_models_apply_to_every_assistance_task_and_disable_all_context_tasks()
    {
        var choices = new Dictionary<string, string>
        {
            ["assistance"] = "shared-assistant", ["completion"] = "code-model", ["context"] = "off", ["embedding"] = "off",
        };
        var featureModels = SmartFeatureSettings.Features.ToDictionary(f => f.Id, f => choices[SmartFeatureSettings.GroupFor(f.Id)]);
        int requests = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("shared-assistant", body.RootElement.GetProperty("model").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        }));
        var client = new SmartProviderClient("openai", Profile(), featureModels, http);
        foreach (var feature in new[] { "commit", "shell", "assess", "routing", "brief" })
            Assert.Equal("ok", await client.CompleteAsync(feature, "s", "p", 64, default));
        foreach (var feature in new[] { "summary", "context", "ranking" }) Assert.False(client.IsFeatureEnabled(feature));
        Assert.Equal("code-model", featureModels["completion"]);
        Assert.Equal(5, requests);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static ByokProvider Profile() => new() { Id = "profile", Name = "Test", BaseUrl = "https://example.test/v1", AnthropicBaseUrl = "https://example.test/v1", ApiKey = "test-key", FlashModel = "default-model" };

    [Theory]
    [InlineData("openai", "chat/completions")]
    [InlineData("anthropic", "messages")]
    public async Task Generation_uses_selected_feature_model_endpoint_and_protocol(string provider, string suffix)
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
        var client = new SmartProviderClient(provider, Profile(), new Dictionary<string, string> { ["commit"] = "commit-model", ["shell"] = "shell-model" }, http);
        Assert.Equal("feat: add setting", await client.CompleteAsync("commit", "system", "diff", 64, default));
    }

    [Fact]
    public async Task Blank_model_uses_profile_default_and_off_feature_never_sends()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("default-model", body.RootElement.GetProperty("model").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        }));
        var client = new SmartProviderClient("openai", Profile(), new Dictionary<string, string> { ["completion"] = "off" }, http);
        Assert.Equal("ok", await client.CompleteAsync("commit", "system", "input", 64, default));
        Assert.False(client.IsFeatureEnabled("completion"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync("completion", "system", "input", 64, default));
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
        var models = new Dictionary<string, string> { ["embedding"] = "embedding-model" };
        var client = new SmartProviderClient("openai", Profile(), models, http);
        Assert.Equal(new[] { 0.6f, 0.8f }, await client.EmbedAsync("code", default));
        var other = Profile(); other.BaseUrl = "https://other.test/v1";
        Assert.NotEqual(client.EmbeddingIdentity, new SmartProviderClient("openai", other, models).EmbeddingIdentity);
        foreach (var provider in new[] { "off", "claude", "codex", "anthropic" })
        {
            var unsupported = new SmartProviderClient(provider, Profile(), models, http);
            Assert.Equal("off", unsupported.EmbeddingIdentity);
            Assert.Empty(await unsupported.EmbedAsync("code", default));
        }
    }

    [Fact]
    public async Task Http_errors_do_not_expose_credentials_or_server_body()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("test-key: secret server detail") })));
        var client = new SmartProviderClient("openai", Profile(), new Dictionary<string, string>(), http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("commit", "s", "p", 64, default));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain("secret server detail", error.Message);
    }
}
