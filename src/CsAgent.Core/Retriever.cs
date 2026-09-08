using Microsoft.Extensions.AI;

namespace CsAgent.Core;

public sealed record CitedChunk(
    [property: System.Text.Json.Serialization.JsonPropertyName("number")] int Number,
    [property: System.Text.Json.Serialization.JsonPropertyName("page_path")] string PagePath,
    [property: System.Text.Json.Serialization.JsonPropertyName("ordinal")] int Ordinal,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("score")] float Score);

/// <summary>
/// Query embed + MMR diversity selection over the store's full scored pool;
/// chunks numbered [1..k] relevance-descending for citation. The query embed
/// is bounded by the shared model-call ceiling — a stalled embedding route
/// is a structured error, never a wedge.
/// </summary>
public sealed class Retriever(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    SqliteVectorStore store,
    int topK,
    TimeSpan? embedCallTimeout = null)
{
    /// <summary>Relevance/diversity trade-off — fixed, not env-tunable (agreed 2026-09-08).</summary>
    public const float Lambda = 0.7f;

    private readonly TimeSpan _embedCallTimeout = embedCallTimeout ?? ModelCallTimeout.Default;

    public IReadOnlyList<CitedChunk> Retrieve(string question, CancellationToken cancellationToken = default)
    {
        var vector = ModelCallTimeout.Await("embedding",
            ct => embeddings.GenerateVectorAsync(question, cancellationToken: ct),
            _embedCallTimeout, cancellationToken);
        var pool = store.Pool(vector.Span);
        var selected = MmrSelect(vector.Span, pool, topK, Lambda);
        return [.. selected.Select((c, i) => new CitedChunk(i + 1, c.Hit.PagePath, c.Hit.Ordinal, c.Hit.Text, c.Hit.Score))];
    }

    /// <summary>
    /// MMR selection (Carbonell &amp; Goldstein 1998): each pick maximizes
    /// lambda * relevance(query, candidate) - (1 - lambda) * max similarity to
    /// anything already selected. First pick: the selected set is empty so the
    /// penalty term is 0 — the highest-relevance candidate. An empty pool
    /// returns an empty list (defensive; Retrieve throws empty-store first).
    /// Returns the selected SET, relevance-descending (ties keep pool order).
    /// </summary>
    public static IReadOnlyList<ChunkCandidate> MmrSelect(
        ReadOnlySpan<float> query, IReadOnlyList<ChunkCandidate> pool, int k, float lambda)
    {
        if (pool.Count == 0 || k <= 0)
            return [];

        var count = Math.Min(k, pool.Count);
        var relevance = new float[pool.Count];
        for (var i = 0; i < pool.Count; i++)
            relevance[i] = VectorMath.Cosine(query, pool[i].Vector);

        var remaining = Enumerable.Range(0, pool.Count).ToList();
        var picked = new List<int>(count);
        while (picked.Count < count)
        {
            var bestAt = 0;
            var bestMargin = float.MinValue;
            for (var r = 0; r < remaining.Count; r++)
            {
                var i = remaining[r];
                var maxSim = 0f;
                foreach (var p in picked)
                    maxSim = MathF.Max(maxSim, VectorMath.Cosine(pool[i].Vector, pool[p].Vector));
                var margin = lambda * relevance[i] - (1f - lambda) * maxSim;
                if (margin > bestMargin)
                {
                    bestMargin = margin;
                    bestAt = r;
                }
            }
            picked.Add(remaining[bestAt]);
            remaining.RemoveAt(bestAt);
        }

        return [.. picked
            .OrderByDescending(i => relevance[i])
            .ThenBy(i => i)
            .Select(i => pool[i])];
    }
}