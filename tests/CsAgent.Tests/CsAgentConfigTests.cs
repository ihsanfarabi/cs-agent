using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public class CsAgentConfigTests
{
    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void MissingModelKey_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() =>
            CsAgentConfig.FromEnvironment(Env(("CS_AGENT_MODEL_KEY", null)), new HashSet<string> { "fixtures" }));
        Assert.Equal("config", ex.Error.Component);
        Assert.Equal("missing-required-env", ex.Error.Code);
        Assert.Contains("CS_AGENT_MODEL_KEY", ex.Error.Message);
    }

    [Fact]
    public void NonIntegerTopK_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() =>
            CsAgentConfig.FromEnvironment(
                Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_TOP_K", "five")), new HashSet<string> { "fixtures" }));
        Assert.Contains("CS_AGENT_TOP_K", ex.Error.Message);
    }

    [Fact]
    public void OutOfRangeChunkSize_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() =>
            CsAgentConfig.FromEnvironment(
                Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_CHUNK_SIZE", "10")), new HashSet<string> { "fixtures" }));
        Assert.Contains("CS_AGENT_CHUNK_SIZE", ex.Error.Message);
    }

    [Fact]
    public void Defaults_AppliedWhenUnset()
    {
        var config = CsAgentConfig.FromEnvironment(Env(("CS_AGENT_MODEL_KEY", "k")), new HashSet<string> { "fixtures" });
        Assert.Equal(8, config.TopK);
        Assert.Equal(1200, config.ChunkSize);
        Assert.Equal("https://openrouter.ai/api/v1", config.BaseUrl);
    }

    [Fact]
    public void EscalationWebhook_UnsetOrWhitespace_FeatureOff()
    {
        // feature is opt-in: unset or whitespace ⇒ null ⇒ byte-identical behavior
        Assert.Null(CsAgentConfig.FromEnvironment(
            Env(("CS_AGENT_MODEL_KEY", "k")), new HashSet<string> { "fixtures" }).EscalationWebhook);
        Assert.Null(CsAgentConfig.FromEnvironment(
            Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_ESCALATION_WEBHOOK", "   ")),
            new HashSet<string> { "fixtures" }).EscalationWebhook);
    }

    [Fact]
    public void EscalationWebhook_AbsoluteHttpUrl_StoredAndKeyListed()
    {
        Assert.Contains("CS_AGENT_ESCALATION_WEBHOOK", CsAgentConfig.EnvKeys); // every surface forwards it

        var https = CsAgentConfig.FromEnvironment(
            Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_ESCALATION_WEBHOOK", "https://hooks.example.com/x")),
            new HashSet<string> { "fixtures" });
        Assert.Equal(new Uri("https://hooks.example.com/x"), https.EscalationWebhook);

        var http = CsAgentConfig.FromEnvironment(
            Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_ESCALATION_WEBHOOK", "http://localhost:9999/hook")),
            new HashSet<string> { "fixtures" });
        Assert.Equal(new Uri("http://localhost:9999/hook"), http.EscalationWebhook);
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void EscalationWebhook_InvalidUrl_ThrowsNamedError(string value)
    {
        var ex = Assert.Throws<CsAgentException>(() => CsAgentConfig.FromEnvironment(
            Env(("CS_AGENT_MODEL_KEY", "k"), ("CS_AGENT_ESCALATION_WEBHOOK", value)),
            new HashSet<string> { "fixtures" }));
        Assert.Equal("config", ex.Error.Component);
        Assert.Equal("invalid-env-value", ex.Error.Code);
        Assert.Contains("CS_AGENT_ESCALATION_WEBHOOK must be an absolute http(s) URL", ex.Error.Message);
        Assert.Contains($"got \"{value}\"", ex.Error.Message);
    }
}