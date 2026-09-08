using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public sealed class SqliteVectorStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void UpsertThenPool_ReturnsBestMatchFirst()
    {
        using (var store = new SqliteVectorStore(_dbPath, "text-embedding-3-small"))
        {
            store.UpsertPage("docs/api-keys.md", "h1", new[]
            {
                ("Rotating keys from Settings", new float[] { 1, 0, 0 }),
                ("Billing plans overview", new float[] { 0, 1, 0 }),
            });
        }

        using (var store = new SqliteVectorStore(_dbPath, "text-embedding-3-small"))
        {
            var pool = store.Pool(new float[] { 0.9f, 0.1f, 0 });
            var hit = pool[0];
            Assert.Equal("docs/api-keys.md", hit.Hit.PagePath);
            Assert.Equal("Rotating keys from Settings", hit.Hit.Text);
            Assert.True(hit.Hit.Score > 0.99f);
        }
    }

    [Fact]
    public void IdempotentReingest_NoDuplicateChunks()
    {
        using (var store = new SqliteVectorStore(_dbPath, "m"))
        {
            store.UpsertPage("a.md", "h1", new[] { ("one", new float[] { 1, 0 }) });
            store.UpsertPage("a.md", "h1", new[] { ("one", new float[] { 1, 0 }), ("two", new float[] { 0, 1 }) });
        }
        using (var store = new SqliteVectorStore(_dbPath, "m"))
        {
            Assert.Equal(2, store.Pool(new float[] { 1, 0 }).Count);
        }
    }

    [Fact]
    public void EmptyStore_Pool_ThrowsNamedError()
    {
        using var store = new SqliteVectorStore(_dbPath, "m");
        var ex = Assert.Throws<CsAgentException>(() => store.Pool(new float[] { 1, 0 }));
        Assert.Equal("empty-store", ex.Error.Code);
        Assert.Contains("ingest", ex.Error.Message);
    }

    [Fact]
    public void Pool_ReturnsAllChunks_RelevanceOrdered_WithVectors()
    {
        using var store = new SqliteVectorStore(_dbPath, "m");
        store.UpsertPage("a.md", "h1", new[]
        {
            ("one", new float[] { 1, 0 }),
            ("two", new float[] { 0, 1 }),
        });

        var pool = store.Pool(new float[] { 0.9f, 0.1f });

        Assert.Equal(2, pool.Count);                        // whole scored set, not top-k
        Assert.Equal("one", pool[0].Hit.Text);              // relevance-descending
        Assert.Equal(2, pool[0].Vector.Length);              // vectors retained
        Assert.True(pool[0].Hit.Score >= pool[1].Hit.Score); // scores monotonic
    }

    [Fact]
    public void CorruptDb_ThrowsNamedErrorSuggestingReingest()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database");
        var ex = Assert.Throws<CsAgentException>(() => new SqliteVectorStore(_dbPath, "m"));
        Assert.Equal("corrupt-store", ex.Error.Code);
        Assert.Contains("re-ingest", ex.Error.Message);
    }

    [Fact]
    public void EmbeddingModelMismatch_ThrowsNamedError()
    {
        using (new SqliteVectorStore(_dbPath, "text-embedding-3-small"))
        {
        }
        var ex = Assert.Throws<CsAgentException>(() => new SqliteVectorStore(_dbPath, "other-model"));
        Assert.Equal("embedding-model-mismatch", ex.Error.Code);
        Assert.Contains("re-ingest", ex.Error.Message);
    }

    [Fact]
    public void EmptyPath_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() => new SqliteVectorStore("", "m"));
        Assert.Equal("missing-store-path", ex.Error.Code);
    }
}