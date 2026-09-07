using CsAgent.Core;
using Microsoft.Agents.AI;
using Xunit;

namespace CsAgent.Tests;

public sealed class AskPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-ask-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private void SeedStore()
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        store.UpsertPage("docs/api-keys.md", "h1", new[]
        {
            ("Rotate keys from Settings → API keys → Rotate.", FakeEmbeddingGenerator.HashToVector("rotate settings")),
            ("Keys inherit the team role. Read-only keys cannot deploy.", FakeEmbeddingGenerator.HashToVector("roles permissions")),
        });
    }

    private (AskPipeline Pipeline, FakeChatClient DraftClient, FakeChatClient VerifyClient) MakePipeline(
        string draftAnswer, string verifyJson, string verifyJsonRetry)
    {
        SeedStore();
        var embeddings = new FakeEmbeddingGenerator();
        var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        var retriever = new Retriever(embeddings, store, topK: 5);

        var draftClient = new FakeChatClient(_ => draftAnswer);
        var callIndex = 0;
        var verifyClient = new FakeChatClient(_ => ++callIndex == 1 ? verifyJson : verifyJsonRetry);

        ChatClientAgent Draft() => new(draftClient);
        ChatClientAgent Verify() => new(verifyClient);

        return (new AskPipeline(Draft, Verify, retriever), draftClient, verifyClient);
    }

    private const string ResolvedJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"Old keys stay valid 24 hours","supported":true,"supporting_chunk_ids":[1]}]""";
    private const string EscalateJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"SLA uptime is 99.9%","supported":false,"supporting_chunk_ids":[]}]""";

    [Fact]
    public void ResolvedEndToEnd()
    {
        var (pipeline, draft, verify) = MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]",
            ResolvedJson, ResolvedJson);

        var result = pipeline.Run("How do I rotate the API key?");
        Assert.True(result.Resolved);
        Assert.NotNull(result.Answer);
        Assert.Equal(2, result.CitedChunks.Count); // top-k returns both seeded chunks
        Assert.Equal(2, result.Calls);
        Assert.Equal(1, draft.CallCount);
        Assert.Equal(1, verify.CallCount); // no retry on valid JSON
    }

    [Fact]
    public void UnsupportedClaim_EscalatesWithMissing()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.",
            EscalateJson, EscalateJson);

        var result = pipeline.Run("What is your SLA uptime?");
        Assert.False(result.Resolved);
        var missing = Assert.Single(result.Missing!);
        Assert.Contains("SLA", missing.Claim);
        Assert.Null(result.Answer); // rejected draft never surfaces
        Assert.NotEmpty(result.Claims);
    }

    [Fact]
    public void MalformedTwice_FailsClosedToEscalate()
    {
        var (pipeline, draft, verify) = MakePipeline(
            "Rotate from Settings [1].",
            "THIS IS NOT JSON", "STILL NOT JSON");

        var result = pipeline.Run("How do I rotate keys?");
        Assert.False(result.Resolved);
        Assert.Null(result.Answer);
        Assert.Equal(3, result.Calls); // draft + 2 verify attempts
        Assert.Equal(2, verify.CallCount);
        var missing = Assert.Single(result.Missing!);
        Assert.Contains("malformed", missing.Note);
    }

    [Fact]
    public void MalformedThenValid_Resolves()
    {
        var (pipeline, _, verify) = MakePipeline(
            "Rotate from Settings [1].",
            "NOT JSON", ResolvedJson);

        var result = pipeline.Run("How do I rotate keys?");
        Assert.True(result.Resolved);
        Assert.Equal(2, verify.CallCount); // retried exactly once
    }

    [Fact]
    public void ZeroClaims_Escalates()
    {
        var (pipeline, _, _) = MakePipeline(
            "The ingested docs do not answer this.",
            "[]", "[]");

        var result = pipeline.Run("What is your SLA uptime?");
        Assert.False(result.Resolved);
        var missing = Assert.Single(result.Missing!);
        Assert.Equal("What is your SLA uptime?", missing.Claim);
        Assert.Contains("no answerable claim", missing.Note);
    }

    [Fact]
    public void CancelledBeforeRun_ThrowsBeforeAnyModelCall()
    {
        var (pipeline, draft, verify) = MakePipeline("Rotate from Settings [1].", ResolvedJson, ResolvedJson);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => pipeline.Run("How do I rotate keys?", cts.Token));
        Assert.Equal(0, draft.CallCount);
        Assert.Equal(0, verify.CallCount);
    }

    [Fact]
    public void RunWithEvidence_Resolved_CapturesDraftAndRawVerifierJson()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]",
            ResolvedJson, ResolvedJson);

        var run = pipeline.RunWithEvidence("How do I rotate the API key?");
        Assert.True(run.Result.Resolved);
        Assert.False(run.Evidence.DraftRejected);
        Assert.Equal(run.Result.Answer, run.Evidence.DraftAnswer); // accepted draft, identical
        Assert.Equal(ResolvedJson, run.Evidence.VerifierRaw);       // raw verifier output, exact
    }

    [Fact]
    public void RunWithEvidence_Escalated_RejectedDraftAndRawCaptured()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.",
            EscalateJson, EscalateJson);

        var run = pipeline.RunWithEvidence("What is your SLA uptime?");
        Assert.False(run.Result.Resolved);
        Assert.Null(run.Result.Answer); // canonical surface keeps the rejection hidden
        Assert.Equal("Rotate from Settings [1]. SLA uptime is 99.9%.", run.Evidence.DraftAnswer);
        Assert.True(run.Evidence.DraftRejected); // the trace labels it gated, explicitly
        Assert.Equal(EscalateJson, run.Evidence.VerifierRaw);
    }

    [Fact]
    public void RunWithEvidence_MalformedTwice_RawIsLastVerifierOutput()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate from Settings [1].", "THIS IS NOT JSON", "STILL NOT JSON");

        var run = pipeline.RunWithEvidence("How do I rotate keys?");
        Assert.False(run.Result.Resolved);
        Assert.Equal(3, run.Result.Calls); // draft + 2 verify attempts — no new calls
        Assert.True(run.Evidence.DraftRejected);
        // the trace shows WHAT was malformed — that is the audit value
        Assert.Equal("STILL NOT JSON", run.Evidence.VerifierRaw);
    }

    [Fact]
    public void RunWithEvidence_VerifierTransportThrows_RawNullStillEscalates()
    {
        // verify transport failures fail closed (existing behavior); no response
        // object existed, so raw is null — never reconstructed (honest evidence)
        SeedStore();
        var retriever = new Retriever(
            new FakeEmbeddingGenerator(), new SqliteVectorStore(_dbPath, "fake-embedding"), topK: 5);
        ChatClientAgent Draft() => new(new FakeChatClient(_ => "Rotate from Settings [1]."));
        ChatClientAgent Verify() => new(new FakeChatClient(
            _ => throw new HttpRequestException("connection refused")));
        var pipeline = new AskPipeline(Draft, Verify, retriever);

        var run = pipeline.RunWithEvidence("How do I rotate keys?");
        Assert.False(run.Result.Resolved);
        Assert.True(run.Evidence.DraftRejected);
        Assert.Null(run.Evidence.VerifierRaw);
    }

    [Fact]
    public void CancelledDuringVerify_PropagatesInsteadOfFailingClosed()
    {
        SeedStore();
        var embeddings = new FakeEmbeddingGenerator();
        var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        var retriever = new Retriever(embeddings, store, topK: 5);
        var draftClient = new FakeChatClient(_ => "Rotate from Settings [1].");
        using var cts = new CancellationTokenSource();
        var verifyClient = new FakeChatClient(_ =>
        {
            cts.Cancel(); // server shutdown lands mid-verify
            throw new OperationCanceledException(cts.Token);
        });

        ChatClientAgent Draft() => new(draftClient);
        ChatClientAgent Verify() => new(verifyClient);
        var pipeline = new AskPipeline(Draft, Verify, retriever);

        // shutdown propagates — the job dies as cancelled, never fabricates a
        // fail-closed verdict (non-cancellation failures still fail closed)
        Assert.Throws<OperationCanceledException>(() => pipeline.Run("How do I rotate keys?", cts.Token));
        Assert.Equal(1, verifyClient.CallCount); // no retry on cancellation
    }
}