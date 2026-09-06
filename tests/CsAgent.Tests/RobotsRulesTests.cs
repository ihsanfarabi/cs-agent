using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public sealed class RobotsRulesTests
{
    [Fact]
    public void DisallowPrefix_BlocksMatchingPathsOnly()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /private\n");
        Assert.False(rules.IsAllowed("/private/page"));
        Assert.False(rules.IsAllowed("/private"));
        Assert.True(rules.IsAllowed("/public"));
        Assert.True(rules.IsAllowed("/"));
    }

    [Fact]
    public void StarAndAgentGroups_Union()
    {
        var rules = RobotsRules.Parse(
            "User-agent: *\nDisallow: /a\n\nUser-agent: cs-agent\nDisallow: /b\n");
        Assert.False(rules.IsAllowed("/a/x"));
        Assert.False(rules.IsAllowed("/b/x"));
        Assert.True(rules.IsAllowed("/c"));
    }

    [Fact]
    public void EmptyDisallow_AllowsAll()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow:\n");
        Assert.True(rules.IsAllowed("/private"));
    }

    [Fact]
    public void EmptyBody_AllowsAll()
    {
        var rules = RobotsRules.Parse("");
        Assert.True(rules.IsAllowed("/anything"));
        Assert.Null(rules.CrawlDelay);
    }

    [Fact]
    public void CrawlDelay_ParsedAsLargestAcrossGroups()
    {
        var rules = RobotsRules.Parse(
            "User-agent: *\nCrawl-delay: 2\n\nUser-agent: cs-agent\nCrawl-delay: 5\n");
        Assert.Equal(TimeSpan.FromSeconds(5), rules.CrawlDelay);
    }

    [Fact]
    public void CommentsAndCaseIgnored_OtherAgentsSkipped()
    {
        var rules = RobotsRules.Parse("# robots\nuser-agent: gptbot\ndisallow: /\n\nuser-agent: *\ndisallow: /x\n");
        Assert.True(rules.IsAllowed("/"));
        Assert.False(rules.IsAllowed("/x/y"));
    }
}