using System.Text.RegularExpressions;
using CsAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// The CI eval gate (TODOS P2): a scripted full-fixture eval over the fake
/// model seam, wired exactly like AskPipelineTests but through EvalRunner —
/// scoring, fixture loading, resume bookkeeping, and pipeline wiring all ride
/// the same `dotnet test` run that CI asserts on.
///
/// Honest scope (D2): the stub responders ignore prompt text, so this gate
/// catches HARNESS regressions only. It cannot catch prompt-tuning
/// regressions — those need the live eval (manual workflow_dispatch job).
/// </summary>
public sealed partial class EvalGateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-eval-gate-{Guid.NewGuid():N}.db");
    private readonly string _resultsDir = Path.Combine(Path.GetTempPath(), $"cs-agent-eval-gate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_resultsDir)) Directory.Delete(_resultsDir, recursive: true);
    }

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>
    /// Full scripted run: every resolved question drafts a citation into an
    /// expected page and verifies clean; every escalate question verifies one
    /// unsupported claim. `escalateFlips` inverts a question's script so the
    /// negative control can prove the gate is not vacuous.
    /// </summary>
    private (EvalMetrics Metrics, IReadOnlyList<EvalQuestionResult> Results, int Skipped) RunScripted(
        IReadOnlyCollection<string> escalateFlips)
    {
        var records = FixtureLoader.FromJsonl(Path.Combine(FixtureRoot, "questions.jsonl"));
        Assert.Equal(25, records.Count); // fixture sanity — loader already fails loudly on corruption

        var byQuestion = records.ToDictionary(r => r.Question, r => r);
        var flips = new HashSet<string>(escalateFlips);

        // Ingest the fixture corpus with the same recipe the CLI eval uses
        // (title-prefixed chunks, default 1200/150), on deterministic
        // 8-dim hash embeddings.
        var embeddings = new FakeEmbeddingGenerator();
        using (var ingestStore = new SqliteVectorStore(_dbPath, "fake-embedding"))
        {
            new IngestPipeline(embeddings, ingestStore, chunkSize: 1200, chunkOverlap: 150)
                .Run(Path.Combine(FixtureRoot, "docs"), "fixtures");
        }

        // Fake hash embeddings carry no semantic signal, so ranking is
        // arbitrary — a top-8 window may not contain a record's expected
        // page. The gate tests wiring, not ranking, so retrieve the whole
        // corpus and let the scripts pick the expected page.
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        var retriever = new Retriever(embeddings, store, topK: 500);

        ChatClientAgent Draft() => new(new FakeChatClient(messages =>
        {
            var record = byQuestion[QuestionOf(messages)];
            if (record.ExpectedOutcome != "resolved")
                return "The ingested docs do not answer this.";
            var chunk = ExpectedChunk(messages, record);
            return $"stub draft citing [{chunk.number}] ({chunk.page})";
        }));
        ChatClientAgent Verify() => new(new FakeChatClient(messages =>
        {
            var record = byQuestion[QuestionOf(messages)];
            var escalate = record.ExpectedOutcome == "escalate" ^ flips.Contains(record.Question);
            if (escalate)
                return """[{"claim":"stub","supported":false,"supporting_chunk_ids":[],"unsupported_by":[]}]""";
            var chunk = ExpectedChunk(messages, record);
            return $$"""[{"claim":"stub","supported":true,"supporting_chunk_ids":[{{chunk.number}}],"unsupported_by":[]}]""";
        }));

        var pipeline = new AskPipeline(Draft, Verify, retriever);
        var runner = new EvalRunner(pipeline.Run, _resultsDir);
        return runner.Run(records, fresh: true);
    }

    /// <summary>
    /// First retrieved chunk whose page is an expected source for the record;
    /// first chunk of the window when the record names none (a flipped
    /// escalate record has empty expected_sources).
    /// </summary>
    private static (int number, string page) ExpectedChunk(IEnumerable<ChatMessage> messages, FixtureRecord record)
    {
        var text = string.Join("\n", messages.Select(m => m.Text));
        var chunks = ChunkLineRegex().Matches(text)
            .Select(m => (number: int.Parse(m.Groups[1].Value), page: m.Groups[2].Value))
            .ToList();
        var expected = chunks.FirstOrDefault(c => record.ExpectedSources.Contains(c.page));
        return expected.number == 0 ? chunks[0] : expected;
    }

    private static string QuestionOf(IEnumerable<ChatMessage> messages)
    {
        var text = string.Join("\n", messages.Select(m => m.Text));
        return QuestionLineRegex().Match(text).Groups[1].Value;
    }

    [GeneratedRegex(@"^QUESTION: (.+)$", RegexOptions.Multiline)]
    private static partial Regex QuestionLineRegex();

    [GeneratedRegex(@"^\[(\d+)\] \(([^)]+)\)", RegexOptions.Multiline)]
    private static partial Regex ChunkLineRegex();

    [Fact]
    public void ScriptedEval_MeetsAllPassTargets()
    {
        var (metrics, results, skipped) = RunScripted(escalateFlips: []);

        Assert.Equal(25, results.Count);
        Assert.Equal(0, skipped);
        Assert.Equal(20, metrics.AnswerableTotal);
        Assert.Equal(20, metrics.AnswerableResolved);
        Assert.Equal(5, metrics.UnanswerableTotal);
        Assert.Equal(5, metrics.UnanswerableEscalated);
        Assert.Equal(0, metrics.HallucinationProxy);
        Assert.True(EvalScoring.Passes(metrics),
            $"gate must pass on the scripted fixture, got {metrics.AnswerableResolved}/{metrics.AnswerableTotal} answerable, " +
            $"{metrics.UnanswerableEscalated}/{metrics.UnanswerableTotal} unanswerable, proxy {metrics.HallucinationProxy}");
    }

    /// <summary>
    /// Negative control: flip ONE escalate question's script to resolve. The
    /// unanswerable target demands ALL five escalate, so a single flip must
    /// fail the gate — proving Passes is not vacuous. (A resolved→escalate
    /// flip would leave 19/20 = 95% ≥ the 80% answerable target and NOT fail.)
    /// </summary>
    [Fact]
    public void OneEscalateFlip_FailsTheGate()
    {
        var flipped = FixtureLoader.FromJsonl(Path.Combine(FixtureRoot, "questions.jsonl"))
            .First(r => r.ExpectedOutcome == "escalate").Question;

        var (metrics, _, _) = RunScripted([flipped]);

        Assert.Equal(4, metrics.UnanswerableEscalated);
        Assert.Equal(1, metrics.HallucinationProxy); // resolved on an unanswerable record = zero-overlap citation
        Assert.False(EvalScoring.Passes(metrics));
    }
}