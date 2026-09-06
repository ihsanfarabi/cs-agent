using System.Text.Json;
using CsAgent.Core;
using CsAgent.Mcp;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// MCP surface tests (the stdio round-trip itself is exercised against a real
/// server out-of-band). CEO-review contract: the round trip exposes claims[]
/// and estimated_cost — a refactor that drops the gate's reasoning payload
/// from the MCP surface fails here.
/// </summary>
public class CsAgentToolTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-mcp-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private (CsAgentToolbox Toolbox, FakeChatClient Draft, FakeChatClient Verify) MakeToolbox(
        string draftAnswer, string verifyJson)
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        store.UpsertPage("docs/api-keys.md", "h1", new[]
        {
            ("Rotate keys from Settings → API keys → Rotate.", FakeEmbeddingGenerator.HashToVector("rotate settings")),
        });

        var draft = new FakeChatClient(_ => draftAnswer);
        var verify = new FakeChatClient(_ => verifyJson);
        var embeddings = new FakeEmbeddingGenerator();
        var retriever = new Retriever(embeddings, new SqliteVectorStore(_dbPath, "fake-embedding"), topK: 5);
        var pipeline = new AskPipeline(
            () => new Microsoft.Agents.AI.ChatClientAgent(draft),
            () => new Microsoft.Agents.AI.ChatClientAgent(verify),
            retriever);
        return (new CsAgentToolbox(() => pipeline), draft, verify);
    }

    private const string ResolvedJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]}]""";

    [Fact]
    public void AskTool_Resolved_ReturnsFullResultObject()
    {
        var (toolbox, _, _) = MakeToolbox(
            "Rotate keys from Settings [1].", ResolvedJson);

        var json = CsAgentTools.Ask(toolbox, "How do I rotate the API key?");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("resolved").GetBoolean());
        Assert.NotEmpty(root.GetProperty("answer").GetString()!);
        Assert.True(root.TryGetProperty("estimated_cost", out var cost)); // present
        Assert.NotEqual(JsonValueKind.Null, cost.ValueKind);             // and populated
        var claims = root.GetProperty("claims");
        Assert.Equal(JsonValueKind.Array, claims.ValueKind);
        Assert.Equal(1, claims.GetArrayLength());
        Assert.True(claims[0].GetProperty("supported").GetBoolean());
        Assert.True(root.TryGetProperty("citations", out _));
        Assert.True(root.TryGetProperty("calls", out _));
        Assert.True(root.TryGetProperty("seconds", out _));
    }

    [Fact]
    public void AskTool_Escalate_NeverIncludesRejectedAnswer()
    {
        var (toolbox, _, _) = MakeToolbox(
            "Rotate from Settings [1]. SLA uptime is 99.9%.",
            """[{"claim":"SLA uptime is 99.9%","supported":false,"supporting_chunk_ids":[]}]""");

        var json = CsAgentTools.Ask(toolbox, "What is your SLA uptime?");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("resolved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("answer").ValueKind);
        Assert.NotEmpty(root.GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public void AskTool_StoreError_ReturnsStructuredErrorObject()
    {
        var toolbox = new CsAgentToolbox(() =>
            CsAgentRuntime.CreateAskPipeline(CsAgentConfig.FromEnvironment(
                new Dictionary<string, string?> { ["CS_AGENT_MODEL_KEY"] = "k" },
                new HashSet<string>(["fixtures"]))));

        var json = CsAgentTools.Ask(toolbox, "anything");
        using var doc = JsonDocument.Parse(json);
        var err = doc.RootElement.GetProperty("error");

        Assert.False(string.IsNullOrWhiteSpace(err.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(err.GetProperty("message").GetString()));
        // one of the named codes for a missing/empty store
        Assert.Contains(err.GetProperty("code").GetString()!,
            new[] { "empty-store", "no-corpus-selected", "corrupt-store", "ask", "missing-required-env" });
    }

    [Fact]
    public void IngestTool_NotConfigured_ReturnsStructuredError()
    {
        // toolbox built without an ingest delegate (test configuration)
        var (toolbox, _, _) = MakeToolbox("x", "[]");
        var json = CsAgentTools.Ingest(toolbox, "/tmp");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not-configured", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}