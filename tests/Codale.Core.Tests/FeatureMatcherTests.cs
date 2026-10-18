using Codale.Core.Features;

namespace Codale.Core.Tests;

public sealed class FeatureMatcherTests
{
    private static FeatureDocument Doc(string id, string name, string text) =>
        new(new FeatureInfo { Id = id, Name = name }, SearchText.ForIndex($"{name} {text}"));

    private static readonly FeatureDocument[] Project =
    [
        Doc("login", "Login", "sign in form password validation session token expired redirect src views LoginPage.xaml.cs"),
        Doc("dl", "Downloads", "download manager progress bar resume queue retry file src views DownloadsPage.xaml.cs"),
        Doc("dash", "Dashboard", "overview charts widgets refresh page layout src views DashboardPage.xaml.cs"),
    ];

    [Fact]
    public void One_word_shared_with_a_feature_is_not_enough()
    {
        // "page" and "update" appear in features, but the message is about neither.
        Assert.Null(FeatureMatcher.Confident("update the page footer text and spacing", Project));
    }

    [Fact]
    public void A_message_about_a_features_own_topic_matches_it()
    {
        var match = FeatureMatcher.Confident("the download queue should retry when the progress bar stalls", Project);

        Assert.Equal("dl", match?.Id);
    }

    [Fact]
    public void The_features_name_alone_can_carry_the_match()
    {
        Assert.Equal("login", FeatureMatcher.Confident("the login looks wrong on small screens", Project)?.Id);
    }

    [Fact]
    public void A_message_that_fits_two_features_equally_matches_neither()
    {
        Assert.Null(FeatureMatcher.Confident("session token for the download queue", Project));
    }

    [Fact]
    public void Ranking_puts_the_features_with_the_most_of_the_message_first()
    {
        var ranked = FeatureMatcher.Rank("charts on the dashboard overview do not refresh", Project);

        Assert.Equal("dash", ranked[0].Feature.Id);
    }

    [Fact]
    public void Nothing_in_common_gives_no_candidates() =>
        Assert.Empty(FeatureMatcher.Rank("explain mutexes", Project));
}
