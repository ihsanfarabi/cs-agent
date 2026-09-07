using CsAgent.Cli;
using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Startup fail-fast for `cs-agent serve` — every path named structured error,
/// exit 1, and never a stray .db left behind (eng-review D3 + outside voice #4).
/// These call ServeRunner.Run directly; Kestrel only starts after every check
/// passes, so the failure paths never bind a socket.
/// </summary>
public sealed class ServeRunnerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-serve-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private CsAgentConfig Config(string embeddingModel = "fake-embedding") =>
        CsAgentConfig.FromEnvironment(new Dictionary<string, string?>
        {
            ["CS_AGENT_MODEL_KEY"] = "test-key",
            ["CS_AGENT_STORE"] = _dbPath,
            ["CS_AGENT_EMBEDDING_MODEL"] = embeddingModel,
        }, new HashSet<string>());

    [Fact]
    public void BadPortFlag_Returns1_NamedError()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port", "not-a-port"], Config()));
    }

    [Fact]
    public void BadPortRange_Returns1()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port", "70000"], Config()));
    }

    [Fact]
    public void MissingArgs_Returns1()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port"], Config()));
    }

    [Fact]
    public void MissingStore_Throws_LeavesNoStrayDb()
    {
        // File.Exists FIRST: constructing the store would EnsureSchema+SetMeta a
        // fresh empty .db — the refusal must not leave that artifact behind
        var ex = Assert.Throws<CsAgentException>(
            () => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("store-missing", ex.Error.Code);
        Assert.False(File.Exists(_dbPath)); // outside voice #4: no stray .db on refusal
    }

    [Fact]
    public void EmptyStore_Throws()
    {
        using (new SqliteVectorStore(_dbPath, "fake-embedding")) { } // schema exists, zero documents

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("empty-store", ex.Error.Code);
    }

    [Fact]
    public void CorruptStore_Throws()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database");

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("corrupt-store", ex.Error.Code);
    }

    [Fact]
    public void EmbeddingModelMismatch_Throws()
    {
        using (var store = new SqliteVectorStore(_dbPath, "model-a"))
        {
            store.UpsertPage("docs/x.md", "h1", new[]
            {
                ("doc text", FakeEmbeddingGenerator.HashToVector("doc")),
            });
        }

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config("model-b")));

        Assert.Equal("embedding-model-mismatch", ex.Error.Code);
    }
}