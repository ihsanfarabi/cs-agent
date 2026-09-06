using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CsAgent.Core;

/// <summary>
/// One hand-labeled eval question. expected_outcome is "resolved" or "escalate";
/// unanswerable questions carry empty expected_sources.
/// </summary>
public sealed record FixtureRecord(
    string Question,
    string ExpectedAnswer,
    IReadOnlyList<string> ExpectedSources,
    string ExpectedOutcome);

/// <summary>
/// Loads fixtures/questions.jsonl. Malformed records fail loudly with the
/// record's line number and the offending field — never a silent skip.
/// </summary>
public static class FixtureLoader
{
    public static IReadOnlyList<FixtureRecord> FromJsonl(string path)
    {
        if (!File.Exists(path))
            throw new CsAgentException(new CsAgentError(
                "eval", "fixture-not-found", $"fixture file not found: {path}"));

        var records = new List<FixtureRecord>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            records.Add(Parse(line, i + 1, path));
        }
        if (records.Count == 0)
            throw new CsAgentException(new CsAgentError(
                "eval", "fixture-empty", $"no records in {path}"));
        return records;
    }

    private static FixtureRecord Parse(string line, int lineNo, string path)
    {
        JsonElement doc;
        try
        {
            doc = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException e)
        {
            throw new CsAgentException(new CsAgentError(
                "eval", "malformed-fixture-record",
                $"record {path}:{lineNo}: not valid JSON ({e.Message})"));
        }

        if (doc.ValueKind != JsonValueKind.Object)
            throw Fail(lineNo, path, "record", "expected a JSON object");

        var question = RequireString(doc, "question", lineNo, path);
        var expectedAnswer = OptionalString(doc, "expected_answer", lineNo, path) ?? "";
        var sources = new List<string>();
        if (doc.TryGetProperty("expected_sources", out var srcEl))
        {
            if (srcEl.ValueKind != JsonValueKind.Array)
                throw Fail(lineNo, path, "expected_sources", "expected an array of strings");
            foreach (var s in srcEl.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.String)
                    throw Fail(lineNo, path, "expected_sources", "expected an array of strings");
                sources.Add(s.GetString()!);
            }
        }
        var outcome = RequireString(doc, "expected_outcome", lineNo, path);
        if (outcome is not ("resolved" or "escalate"))
            throw Fail(lineNo, path, "expected_outcome", $"must be \"resolved\" or \"escalate\", got \"{outcome}\"");
        if (outcome == "escalate" && sources.Count > 0)
            throw Fail(lineNo, path, "expected_sources", "escalate records must have empty expected_sources");
        if (outcome == "resolved" && sources.Count == 0)
            throw Fail(lineNo, path, "expected_sources", "resolved records must name at least one source page");

        return new FixtureRecord(question, expectedAnswer, sources, outcome);
    }

    private static string RequireString(JsonElement doc, string field, int lineNo, string path)
    {
        if (!doc.TryGetProperty(field, out var el))
            throw Fail(lineNo, path, field, "missing field");
        if (el.ValueKind != JsonValueKind.String || el.GetString() is not { Length: > 0 } s)
            throw Fail(lineNo, path, field, "expected a non-empty string");
        return s;
    }

    private static string? OptionalString(JsonElement doc, string field, int lineNo, string path)
    {
        if (!doc.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String)
            throw Fail(lineNo, path, field, "expected a string");
        return el.GetString();
    }

    private static CsAgentException Fail(int lineNo, string path, string field, string why) =>
        new(new CsAgentError(
            "eval", "malformed-fixture-record", $"record {path}:{lineNo}: field {field}: {why}"));

    /// <summary>Stable per-question results-file key (first 16 hex of SHA-256).</summary>
    public static string KeyFor(string question)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(question));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>Scored outcome for one fixture question, persisted per-question for resume.</summary>
public sealed record EvalQuestionResult(
    string Question,
    string ExpectedOutcome,
    bool Resolved,
    IReadOnlyList<string> CitedPages,
    IReadOnlyList<string> ExpectedSources,
    int Calls,
    double Seconds,
    bool Match);

/// <summary>The three metrics plus reported citation accuracy.</summary>
public sealed record EvalMetrics(
    int AnswerableResolved,
    int AnswerableTotal,
    int UnanswerableEscalated,
    int UnanswerableTotal,
    int HallucinationProxy,
    double ResolutionRate,
    double CitationAccuracy);

public static class EvalScoring
{
    /// <summary>Page-level Jaccard between cited pages and expected sources.</summary>
    public static double Jaccard(IReadOnlySet<string> cited, IReadOnlySet<string> expected)
    {
        if (cited.Count == 0 && expected.Count == 0) return 1.0;
        var intersection = cited.Count(c => expected.Contains(c));
        var union = cited.Count + expected.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    public static EvalMetrics Compute(IReadOnlyList<EvalQuestionResult> results)
    {
        var answerable = results.Where(r => r.ExpectedOutcome == "resolved").ToList();
        var unanswerable = results.Where(r => r.ExpectedOutcome == "escalate").ToList();

        var answerableResolved = answerable.Count(r => r.Resolved);
        var unanswerableEscalated = unanswerable.Count(r => !r.Resolved);

        // hallucination proxy: resolved responses whose citations have ZERO
        // page-level overlap with the expected sources. Reproducible by design,
        // independent of the live verifier.
        var proxy = results.Count(r => r.Resolved &&
            !r.CitedPages.Any(c => r.ExpectedSources.Contains(c)));

        var citationAcc = answerable.Where(r => r.Resolved)
            .Select(r => Jaccard(
                new HashSet<string>(r.CitedPages),
                new HashSet<string>(r.ExpectedSources)))
            .DefaultIfEmpty(0.0).Average();

        var total = results.Count;
        var rate = total == 0 ? 0.0 : (double)(answerableResolved + unanswerableEscalated) / total;

        return new EvalMetrics(answerableResolved, answerable.Count,
            unanswerableEscalated, unanswerable.Count, proxy, rate, citationAcc);
    }

    /// <summary>Pass targets: ≥80% of answerable resolved, all unanswerable escalate, proxy 0.</summary>
    public static bool Passes(EvalMetrics m, double answerableTarget = 0.8) =>
        m.AnswerableTotal > 0
        && (double)m.AnswerableResolved / m.AnswerableTotal >= answerableTarget
        && m.UnanswerableEscalated == m.UnanswerableTotal
        && m.UnanswerableTotal > 0
        && m.HallucinationProxy == 0;
}