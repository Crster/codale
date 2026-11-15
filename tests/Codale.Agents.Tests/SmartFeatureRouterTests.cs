namespace Codale.Agents.Tests;

public sealed class SmartFeatureRouterTests
{
    private sealed class FakeClient(string role, bool enabled, string key = "") : ISmartProviderClient
    {
        public string Role => role;
        public bool IsEnabled => enabled;
        public string? ConfigurationError => null;
        public string EmbeddingIdentity => "off";
        public string FeatureIdentity => $"{role}:{key}";
        public string ConfigurationKey => $"{role}:{key}:{enabled}";
        public List<string> Completed { get; } = [];

        public Task<string> CompleteAsync(string task, string system, string input, int tokens, CancellationToken ct)
        {
            Completed.Add(task);
            return Task.FromResult("answer");
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Array.Empty<float>());

        public Task<IReadOnlyDictionary<string, SmartDecision>> DecideAsync(object state,
            IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SmartDecision>>(new Dictionary<string, SmartDecision>());
    }

    private static SmartFeatureRouter Router(params ISmartProviderClient[] clients) =>
        new(clients.ToDictionary(c => c.Role));

    private static ISmartProviderClient Client(string role, bool enabled) => new FakeClient(role, enabled);

    [Fact]
    public void AGeneralTaskIsServedByTheGeneralRole()
    {
        var general = Client(SmartFeatureSettings.General, true);
        var router = Router(general, Client(SmartFeatureSettings.Completion, true));

        Assert.Same(general, router.ClientFor("commit"));
    }

    [Fact]
    public void AnAutocompleteTaskIsServedByTheCompletionRole()
    {
        var completion = Client(SmartFeatureSettings.Completion, true);
        var router = Router(Client(SmartFeatureSettings.General, true), completion);

        Assert.Same(completion, router.ClientFor("completion"));
        Assert.Same(completion, router.ClientFor("shell-completion"));
    }

    [Fact]
    public void AnEmbeddingTaskIsServedByTheEmbeddingRole()
    {
        var embedding = Client(SmartFeatureSettings.Embedding, true);
        var router = Router(Client(SmartFeatureSettings.General, true), embedding);

        Assert.Same(embedding, router.ClientFor("embedding"));
    }

    [Fact]
    public void ATaskWhoseRoleIsOffHasNoClientAndIsNotEnabled()
    {
        var router = Router(Client(SmartFeatureSettings.General, false));

        Assert.Null(router.ClientFor("commit"));
        Assert.False(router.IsFeatureEnabled("commit"));
    }

    [Fact]
    public void ATaskWithNoConfiguredRoleHasNoClient() =>
        Assert.Null(Router().ClientFor("commit"));

    [Fact]
    public void LaneFollowsTheServingRoleAndDefaultsToGeneral()
    {
        var router = Router(Client(SmartFeatureSettings.Completion, true));

        Assert.Equal(SmartFeatureSettings.Completion, router.LaneFor("completion"));
        Assert.Equal(SmartFeatureSettings.General, router.LaneFor("commit"));
    }

    [Fact]
    public void TheDecisionRoleIsTheDeciderOnlyWhenItIsOn()
    {
        var on = Router(Client(SmartFeatureSettings.Decision, true));
        var off = Router(Client(SmartFeatureSettings.Decision, false));

        Assert.NotNull(on.Decider);
        Assert.Null(off.Decider);
    }

    [Fact]
    public void ADecisionTaskIsDecidedOnlyWhenTheDecisionRoleIsOn()
    {
        var on = Router(Client(SmartFeatureSettings.Decision, true));
        var off = Router(Client(SmartFeatureSettings.Decision, false));

        Assert.True(on.Decides("routing"));
        Assert.False(on.Decides("commit"));
        Assert.False(off.Decides("routing"));
    }

    [Fact]
    public void RoutingFallsBackToGeneralWhenTheDecisionRoleIsOff()
    {
        var general = Client(SmartFeatureSettings.General, true);
        var router = Router(general, Client(SmartFeatureSettings.Decision, false));

        Assert.Same(general, router.ClientFor("routing"));
    }

    [Fact]
    public void HasGenerationIsTrueWhenAnyNonEmbeddingFeatureIsOn()
    {
        Assert.True(Router(Client(SmartFeatureSettings.Completion, true)).HasGeneration);
        Assert.False(Router(Client(SmartFeatureSettings.Embedding, true)).HasGeneration);
        Assert.False(Router(Client(SmartFeatureSettings.General, false)).HasGeneration);
    }

    [Fact]
    public async Task CompleteAsyncGoesToTheServingRole()
    {
        var completion = new FakeClient(SmartFeatureSettings.Completion, true);
        var router = Router(new FakeClient(SmartFeatureSettings.General, true), completion);

        var answer = await router.CompleteAsync("completion", "", "", 8, CancellationToken.None);

        Assert.Equal("answer", answer);
        Assert.Equal(["completion"], completion.Completed);
    }

    [Fact]
    public async Task CompleteAsyncRefusesATaskWhoseFeatureIsOff()
    {
        var router = Router(Client(SmartFeatureSettings.General, false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.CompleteAsync("commit", "", "", 8, CancellationToken.None));
    }

    [Fact]
    public void TheConfigurationKeyChangesWhenARoleChanges()
    {
        var before = Router(new FakeClient(SmartFeatureSettings.General, true, "a"));
        var same = Router(new FakeClient(SmartFeatureSettings.General, true, "a"));
        var changed = Router(new FakeClient(SmartFeatureSettings.General, true, "b"));

        Assert.Equal(before.ConfigurationKey, same.ConfigurationKey);
        Assert.NotEqual(before.ConfigurationKey, changed.ConfigurationKey);
    }
}