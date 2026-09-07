using System.Text.Json;

namespace CsAgent.Core;

/// <summary>
/// Runs the ask pipeline over fixture records, persisting one result file per
/// question (idempotent, resumable — same pattern as ingest). Re-runs skip
/// questions whose result file already exists unless fresh=true.
/// </summary>
public sealed class EvalRunner(
    Func<string, AskResult> ask,
    string resultsDir,
    Action<string>? progress = null)
{
    public (EvalMetrics Metrics, IReadOnlyList<EvalQuestionResult> Results, int Skipped) Run(
        IReadOnlyList<FixtureRecord> records, bool fresh = false)
    {
        Directory.CreateDirectory(resultsDir);
        var results = new List<EvalQuestionResult>();
        var skipped = 0;

        foreach (var record in records)
        {
            var file = Path.Combine(resultsDir, $"{FixtureLoader.KeyFor(record.Question)}.json");
            if (!fresh && File.Exists(file))
            {
                results.Add(JsonSerializer.Deserialize<EvalQuestionResult>(
                File.ReadAllText(file), CsAgentJson.SerializerOptions)!);
                skipped++;
                continue;
            }

            progress?.Invoke(record.Question);
            var askResult = ask(record.Question);
            // cited pages = chunks actually referenced by claims (same set the
            // CLI renders as Sources) — not the whole top-k window.
            var citedNumbers = askResult.Claims.SelectMany(c => c.SupportingChunkIds).ToHashSet();
            var citedPages = askResult.CitedChunks
                .Where(c => citedNumbers.Contains(c.Number))
                .Select(c => c.PagePath).Distinct().ToList();
            var result = new EvalQuestionResult(
                record.Question, record.ExpectedOutcome, askResult.Resolved,
                citedPages, record.ExpectedSources, askResult.Calls,
                askResult.Seconds, askResult.Resolved == (record.ExpectedOutcome == "resolved"));
            File.WriteAllText(file, JsonSerializer.Serialize(result, CsAgentJson.SerializerOptions));
            results.Add(result);
        }

        return (EvalScoring.Compute(results), results, skipped);
    }
}