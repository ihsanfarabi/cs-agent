using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public class VerifierRuleTests
{
    private static ClaimVerdict Supported(string claim) => new(claim, true, [1], []);
    private static ClaimVerdict Unsupported(string claim) => new(claim, false, [], []);

    [Fact]
    public void AllSupported_Resolves()
    {
        Assert.True(VerifierRule.Resolve([Supported("a"), Supported("b")]));
    }

    [Fact]
    public void AnyUnsupported_EscalatesWithMissing()
    {
        var claims = new List<ClaimVerdict> { Supported("a"), Unsupported("b") };
        Assert.False(VerifierRule.Resolve(claims));
        var missing = VerifierRule.BuildMissing(claims, "q");
        var item = Assert.Single(missing);
        Assert.Equal("b", item.Claim);
    }

    [Fact]
    public void ZeroClaims_Escalates_NeverVacuouslyResolved()
    {
        Assert.False(VerifierRule.Resolve([]));
        var missing = VerifierRule.BuildMissing([], "What is the SLA?");
        var item = Assert.Single(missing);
        Assert.Equal("What is the SLA?", item.Claim);
        Assert.Contains("no answerable claim", item.Note);
    }

    [Theory]
    [InlineData("not json", false)]
    [InlineData("{\"claim\": \"x\"}", false)]  // object, not array — malformed
    [InlineData("[]", true)]                    // empty array parses; rule escalates zero claims
    [InlineData("[{\"supported\": true}]", false)] // missing claim field — malformed
    [InlineData("```json\n[{\"claim\":\"x\",\"supported\":true}]\n```", true)] // fences tolerated
    public void ParseClaims_EdgeCases(string text, bool valid)
    {
        var parsed = VerifierRule.ParseClaims(text);
        if (valid) Assert.NotNull(parsed);
        else Assert.Null(parsed);
    }
}