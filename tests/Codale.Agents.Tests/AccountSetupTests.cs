using Codale.App.Services;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class AccountSetupTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Detected_user_accounts_use_the_original_cli_home(string cli)
    {
        Assert.Equal(cli == "codex" ? CodaleAccount.UserCodexHome : CodaleAccount.UserClaudeHome,
            CodaleAccount.ProfileHome(cli, CodaleAccount.UserAccountId));
    }

    [Theory]
    [InlineData("{\"loggedIn\":true}", true)]
    [InlineData("{\"loggedIn\":false}", false)]
    [InlineData("{\"loggedIn\":\"false\"}", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("not json", null)]
    public void Claude_status_honours_explicit_signed_out_state(string json, bool? expected) =>
        Assert.Equal(expected, CodaleAccountCli.ReadLoggedIn(json));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removal_signs_out_the_selected_account_before_removing_its_profile(bool succeeds)
    {
        var profile = new ByokProvider { IsAccount = true, CliId = "codex", AccountId = "selected-account" };
        var events = new List<string>();
        var removed = await AccountProfileRemoval.RemoveAsync(profile, p =>
        {
            Assert.Same(profile, p);
            events.Add("sign out selected-account");
            return Task.FromResult(succeeds);
        }, p => { Assert.Same(profile, p); events.Add("remove selected-account"); });
        Assert.Equal(succeeds, removed);
        Assert.Equal(succeeds ? new[] { "sign out selected-account", "remove selected-account" } : new[] { "sign out selected-account" }, events);
    }

    [Fact]
    public async Task A_failed_signout_keeps_the_profile()
    {
        bool removed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountProfileRemoval.RemoveAsync(
            new() { IsAccount = true }, _ => throw new InvalidOperationException("CLI unavailable"), _ => removed = true));
        Assert.False(removed);
    }
}
