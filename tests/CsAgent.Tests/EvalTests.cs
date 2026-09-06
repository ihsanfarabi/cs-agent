using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public class EvalScoringTests
{
    private static EvalQuestionResult R(
        string outcome, bool resolved, string[] cited, string[] expected) =>
        new("q", outcome, resolved, cited, expected, 2, 1.0, resolved == (outcome == "resolved"));

    [Fact]
    public void Compute_CountsAllThreeTargets()
    {
        var results = new List<EvalQuestionResult>
        {
            R("resolved", true, ["a.md"], ["a.md"]),
            R("resolved", true, ["b.md"], ["a.md"]),       // resolved, wrong page — proxy
            R("resolved", false, [], ["a.md"]),            // escalated on answerable
            R("escalate", false, [], []),
            R("escalate", true, ["a.md"], []),             // resolved on unanswerable — proxy
        };
        var m = EvalScoring.Compute(results);
        Assert.Equal(2, m.AnswerableResolved);
        Assert.Equal(3, m.AnswerableTotal);
        Assert.Equal(1, m.UnanswerableEscalated);
        Assert.Equal(2, m.UnanswerableTotal);
        Assert.Equal(2, m.HallucinationProxy);
        Assert.Equal(3.0 / 5, m.ResolutionRate);
    }

    [Fact]
    public void Jaccard_PageLevel()
    {
        Assert.Equal(1.0, EvalScoring.Jaccard(
            new HashSet<string>(["a.md"]), new HashSet<string>(["a.md"])));
        Assert.Equal(0.0, EvalScoring.Jaccard(
            new HashSet<string>(["a.md"]), new HashSet<string>(["b.md"])));
        Assert.Equal(1.0 / 3, EvalScoring.Jaccard(
            new HashSet<string>(["a.md", "b.md"]), new HashSet<string>(["a.md", "c.md"])));
        Assert.Equal(1.0, EvalScoring.Jaccard(
            new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void Passes_GatesOnAllThreeTargets()
    {
        var m = new EvalMetrics(16, 20, 5, 5, 0, 21.0 / 25, 0.9);
        Assert.True(EvalScoring.Passes(m));

        Assert.False(EvalScoring.Passes(m with { AnswerableResolved = 15 })); // 75% < 80%
        Assert.False(EvalScoring.Passes(m with { UnanswerableEscalated = 4 }));
        Assert.False(EvalScoring.Passes(m with { HallucinationProxy = 1 }));
    }

    [Fact]
    public void CitationAccuracy_AveragesJaccardOverAnswered()
    {
        var results = new List<EvalQuestionResult>
        {
            R("resolved", true, ["a.md"], ["a.md"]),        // 1.0
            R("resolved", true, ["a.md", "b.md", "c.md"], ["a.md", "d.md"]), // 1/4 union
            R("escalate", false, [], []),                   // excluded
            R("resolved", false, ["a.md"], ["a.md"]),       // escalated — excluded
        };
        var m = EvalScoring.Compute(results);
        Assert.Equal(0.625, m.CitationAccuracy, precision: 5);
    }
}

public class FixtureLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fx-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Write(params string[] lines) => File.WriteAllLines(_path, lines);

    [Fact]
    public void LoadsValidRecords()
    {
        Write(
            """{"question":"q1","expected_answer":"a","expected_sources":["p.md"],"expected_outcome":"resolved"}""",
            "",
            """{"question":"q2","expected_answer":"","expected_sources":[],"expected_outcome":"escalate"}""");
        var records = FixtureLoader.FromJsonl(_path);
        Assert.Equal(2, records.Count);
        Assert.Equal("resolved", records[0].ExpectedOutcome);
        Assert.Equal("escalate", records[1].ExpectedOutcome);
        Assert.Equal(["p.md"], records[0].ExpectedSources);
    }

    [Fact]
    public void MissingFile_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() => FixtureLoader.FromJsonl(_path));
        Assert.Equal("fixture-not-found", ex.Error.Code);
    }

    [Theory]
    [InlineData("""{"expected_answer":"a","expected_sources":[],"expected_outcome":"escalate"}""", "question")]
    [InlineData("""{"question":"q","expected_answer":"a","expected_sources":[]}""", "expected_outcome")]
    [InlineData("""{"question":"q","expected_answer":"a","expected_sources":["p.md"]}""", "expected_outcome")]
    [InlineData("""{"question":"q","expected_answer":"a","expected_sources":[],"expected_outcome":"maybe"}""", "expected_outcome")]
    [InlineData("""{"question":"q","expected_answer":"a","expected_sources":["p.md"],"expected_outcome":"escalate"}""", "expected_sources")]
    [InlineData("""{"question":5,"expected_answer":"a","expected_sources":[],"expected_outcome":"escalate"}""", "question")]
    [InlineData("not json at all", "record")]
    public void MalformedRecord_NamesLineAndField(string line, string field)
    {
        Write(line);
        var ex = Assert.Throws<CsAgentException>(() => FixtureLoader.FromJsonl(_path));
        Assert.Equal("malformed-fixture-record", ex.Error.Code);
        Assert.Contains(":1:", ex.Error.Message);
        Assert.Contains(field, ex.Error.Message);
    }

    [Fact]
    public void ResolvedRecord_MustNameSource()
    {
        Write("""{"question":"q","expected_answer":"a","expected_sources":[],"expected_outcome":"resolved"}""");
        var ex = Assert.Throws<CsAgentException>(() => FixtureLoader.FromJsonl(_path));
        Assert.Contains("expected_sources", ex.Error.Message);
    }

    [Fact]
    public void KeyFor_IsStable()
    {
        Assert.Equal(FixtureLoader.KeyFor("same question"), FixtureLoader.KeyFor("same question"));
        Assert.NotEqual(FixtureLoader.KeyFor("a"), FixtureLoader.KeyFor("b"));
    }
}

public class EvalRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"eval-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly FixtureRecord[] Records =
    [
        new("q1", "a", ["p.md"], "resolved"),
        new("q2", "", [], "escalate"),
    ];

    private static AskResult AskFor(string question) => new(
        question,
        Resolved: question == "q1",
        Answer: question == "q1" ? "answer" : null,
        Missing: null,
        Claims: question == "q1" ? [new ClaimVerdict("the answer", true, [1], [])] : [],
        CitedChunks: [new CitedChunk(1, "p.md", 0, "text", 0.9f)],
        Calls: 2, Seconds: 0.5, CostEstimate: null);

    [Fact]
    public void ScoresAndPersists()
    {
        var runner = new EvalRunner(AskFor, _dir);
        var (metrics, results, skipped) = runner.Run(Records);
        Assert.Equal(0, skipped);
        Assert.Equal(1, metrics.AnswerableResolved);
        Assert.Equal(1, metrics.UnanswerableEscalated);
        Assert.Equal(0, metrics.HallucinationProxy);
        Assert.True(EvalScoring.Passes(metrics));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Resume_SkipsScoredQuestions()
    {
        var runner = new EvalRunner(AskFor, _dir);
        runner.Run(Records);

        var calls = 0;
        var counting = new EvalRunner(_ => { calls++; return AskFor(_); }, _dir);
        var (_, results, skipped) = counting.Run(Records);
        Assert.Equal(2, skipped);
        Assert.Equal(0, calls);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Fresh_RerunsEverything()
    {
        var runner = new EvalRunner(AskFor, _dir);
        runner.Run(Records);

        var calls = 0;
        var counting = new EvalRunner(_ => { calls++; return AskFor(_); }, _dir);
        var (_, _, skipped) = counting.Run(Records, fresh: true);
        Assert.Equal(0, skipped);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Match_ReflectsOutcomeAgreement()
    {
        var runner = new EvalRunner(AskFor, _dir);
        var (_, results, _) = runner.Run(Records);
        Assert.All(results, r => Assert.True(r.Match));
    }
}