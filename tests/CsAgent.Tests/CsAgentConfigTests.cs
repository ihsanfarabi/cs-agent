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
        Assert.Equal("https://api.openai.com/v1", config.BaseUrl);
    }
}