using System.Diagnostics;
using CsAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Model-call ceiling: a provider route can accept the request, return 200 +
/// headers, and never stream the body (observed live on OpenRouter — the wedged
/// serve held its single-job gate for 20+ minutes with the call in flight and
/// no framework default firing). Each pipeline model call must be bounded:
/// draft timeout = structured error (loud), verify timeout = retry once, then
/// fail closed to escalate.
/// </summary>
public sealed class ModelCallTimeoutTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-timeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private void SeedStore()
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        store.UpsertPage("docs/api-keys.md", "h1", new[]
        {
            ("Rotate keys from Settings.", FakeEmbeddingGenerator.HashToVector("rotate settings")),
        });
    }

    /// <summary>Never completes, ignores cancellation — the observed stall shape.</summary>
    private sealed class StalledChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new TaskCompletionSource<ChatResponse>().Task;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private AskPipeline MakePipeline(IChatClient? draft = null, IChatClient? verify = null)
    {
        SeedStore();
        var embeddings = new FakeEmbeddingGenerator();
        var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        var retriever = new Retriever(embeddings, store, topK: 5);

        ChatClientAgent Draft() => new(draft
            ?? new FakeChatClient(_ => "Rotate keys from Settings. [1]"));
        ChatClientAgent Verify() => new(verify
            ?? new FakeChatClient(_ => """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]}]"""));

        return new AskPipeline(Draft, Verify, retriever,
            modelCallTimeout: TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void DraftStall_ThrowsStructuredModelTimeout()
    {
        var pipeline = MakePipeline(draft: new StalledChatClient());
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.Throws<CsAgentException>(() => pipeline.Run("How do I rotate keys?"));

        Assert.Equal("model", ex.Error.Component);
        Assert.Equal("call-timeout", ex.Error.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"draft timeout must be bounded by the model-call ceiling, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void VerifyStall_RetriesOnceThenFailsClosedToEscalate()
    {
        var stalled = new StalledChatClient();
        var pipeline = MakePipeline(verify: stalled);
        var stopwatch = Stopwatch.StartNew();

        var result = pipeline.Run("How do I rotate keys?");

        Assert.False(result.Resolved);
        var missing = Assert.Single(result.Missing!);
        Assert.Contains("timed out", missing.Note);
        Assert.Equal(2, stalled.CallCount); // one retry, then fail closed
        Assert.Equal(3, result.Calls);      // draft + two verify attempts
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"verify timeouts must be bounded by the model-call ceiling, took {stopwatch.Elapsed}");
    }
}