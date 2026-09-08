using System.Diagnostics;
using CsAgent.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Embed-call ceiling: the same provider stall observed live on chat calls
/// (OpenRouter accepts the request, returns 200 + headers, never streams the
/// body; no framework default fires) can hit the embedding route too. Both
/// embed call sites — the query embed in Retriever and the batch embed in
/// IngestPipeline — must be bounded by the same model-call ceiling as
/// draft/verify: a stall becomes a structured error, never a wedge.
/// </summary>
public sealed class EmbedCallTimeoutTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-embed-timeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>Never completes, ignores cancellation — the observed stall shape.</summary>
    private sealed class StalledEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => new TaskCompletionSource<GeneratedEmbeddings<Embedding<float>>>().Task;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void RetrieverEmbedStall_ThrowsStructuredModelTimeout()
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        store.UpsertPage("docs/api-keys.md", "h1",
            [("Rotate keys from Settings.", FakeEmbeddingGenerator.HashToVector("rotate settings"))]);
        var retriever = new Retriever(new StalledEmbeddingGenerator(), store, topK: 5,
            embedCallTimeout: TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.Throws<CsAgentException>(() => retriever.Retrieve("How do I rotate keys?"));

        Assert.Equal("model", ex.Error.Component);
        Assert.Equal("call-timeout", ex.Error.Code);
        Assert.Contains("embedding", ex.Error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"embed timeout must be bounded by the model-call ceiling, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void IngestEmbedStall_ThrowsStructuredModelTimeout()
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        var pipeline = new IngestPipeline(new StalledEmbeddingGenerator(), store,
            chunkSize: 512, chunkOverlap: 64, embedBatchSize: 64,
            embedCallTimeout: TimeSpan.FromMilliseconds(200));
        var report = new LoadReport([new LoadedPage("docs/api-keys.md", "# API keys\nRotate keys from Settings.")], []);
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.Throws<CsAgentException>(() => pipeline.Run(report, "timeout-probe"));

        Assert.Equal("model", ex.Error.Component);
        Assert.Equal("call-timeout", ex.Error.Code);
        Assert.Contains("embedding", ex.Error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"embed timeout must be bounded by the model-call ceiling, took {stopwatch.Elapsed}");
    }
}