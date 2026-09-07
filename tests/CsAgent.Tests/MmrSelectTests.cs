using CsAgent.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

public sealed class MmrSelectTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-mmr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static ChunkCandidate Candidate(string page, float[] vector, ReadOnlySpan<float> query) =>
        new(new ChunkHit(page, 0, page, VectorMath.Cosine(query, vector)), vector);

    [Fact]
    public void NoRedundancy_MmrSetEqualsPlainTopK_RelevanceOrder()
    {
        float[] query = { 1, 0, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("x", new float[] { 0.9f, 0.1f, 0 }, query),
            Candidate("y", new float[] { 0.5f, 0.8f, 0 }, query),
            Candidate("z", new float[] { 0.2f, 0.3f, 0.9f }, query),
            Candidate("w", new float[] { 0.1f, 0.1f, 0.1f }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 3, Retriever.Lambda);

        // mutual sims are low, so MMR = relevance ranking: x, w, y
        Assert.Equal(new[] { "x", "w", "y" }, selected.Select(c => c.Hit.PagePath).ToArray());
    }

    [Fact]
    public void CloneTwins_CollapseToOneDistinctCandidate()
    {
        float[] query = { 1, 0, 0, 0 };
        var a = Candidate("a", new float[] { 0.9f, 0.43589f, 0, 0 }, query);
        var clone = Candidate("clone", new float[] { 0.89f, 0.456f, 0, 0 }, query);
        var b = Candidate("b", new float[] { 0.85f, -0.1492f, 0.5052f, 0 }, query);
        var c = Candidate("c", new float[] { 0.85f, -0.1492f, 0, 0.5052f }, query);
        var pool = new List<ChunkCandidate> { a, clone, b, c }; // relevance-desc, as Pool returns

        var selected = Retriever.MmrSelect(query, pool, k: 3, Retriever.Lambda);

        Assert.Equal(new[] { "a", "b", "c" }, selected.Select(c => c.Hit.PagePath).ToArray());
        Assert.DoesNotContain(selected, s => s.Hit.PagePath == "clone");
    }

    [Fact]
    public void LambdaDominance_StrongRelevantBeatsWeakDiverse()
    {
        float[] query = { 1, 0, 0, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("a",  new float[] { 0.95f, 0.31225f, 0, 0 }, query),
            Candidate("a2", new float[] { 0.9f, 0.43589f, 0, 0 }, query),
            Candidate("d",  new float[] { 0.3f, 0, 0, 0.95394f }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 2, Retriever.Lambda);

        Assert.Equal(new[] { "a", "a2" }, selected.Select(c => c.Hit.PagePath).ToArray());
    }

    [Fact]
    public void PoolSmallerThanK_ReturnsAll()
    {
        float[] query = { 1, 0 };
        var pool = new List<ChunkCandidate>
        {
            Candidate("a", new float[] { 1, 0 }, query),
            Candidate("b", new float[] { 0, 1 }, query),
        };

        var selected = Retriever.MmrSelect(query, pool, k: 5, Retriever.Lambda);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void EmptyPool_ReturnsEmpty()
    {
        float[] query = { 1, 0 };
        var selected = Retriever.MmrSelect(query, new List<ChunkCandidate>(), k: 3, Retriever.Lambda);
        Assert.Empty(selected);
    }

    [Fact]
    public void Retrieve_NumbersSelectedSet_RelevanceDescending()
    {
        // 8-dim store (FakeEmbeddingGenerator is 8-dim); embedding is fixed so the
        // query vector is exactly (1,0,0,0,0,0,0,0) and the clone scenario applies.
        using (var store = new SqliteVectorStore(_dbPath, "fixed"))
        {
            store.UpsertPage("docs/a.md", "h1", new[]
            {
                ("strong relevant", new float[] { 0.9f, 0.43589f, 0, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/clone.md", "h2", new[]
            {
                ("near-identical twin", new float[] { 0.89f, 0.456f, 0, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/b.md", "h3", new[]
            {
                ("diverse b", new float[] { 0.85f, -0.1492f, 0.5052f, 0, 0, 0, 0, 0 }),
            });
            store.UpsertPage("docs/c.md", "h4", new[]
            {
                ("diverse c", new float[] { 0.85f, -0.1492f, 0, 0.5052f, 0, 0, 0, 0 }),
            });
        }

        using var store2 = new SqliteVectorStore(_dbPath, "fixed");
        var retriever = new Retriever(new FixedEmbeddingGenerator(), store2, topK: 3);

        var cited = retriever.Retrieve("anything");

        Assert.Equal(3, cited.Count);
        Assert.Equal(new[] { "docs/a.md", "docs/b.md", "docs/c.md" },
            cited.Select(c => c.PagePath).ToArray());
        Assert.DoesNotContain(cited, c => c.PagePath == "docs/clone.md");
        Assert.Equal(new[] { 1, 2, 3 }, cited.Select(c => c.Number).ToArray());
        Assert.True(cited[0].Score >= cited[1].Score && cited[1].Score >= cited[2].Score);
    }

    /// <summary>Always returns (1,0,0,0,0,0,0,0) — the seeded scenario's query vector.</summary>
    private sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
