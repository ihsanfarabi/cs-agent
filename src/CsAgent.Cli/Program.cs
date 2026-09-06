using CsAgent.Core;

try
{
    var config = CsAgentConfig.FromCurrentEnvironment(new HashSet<string>(["fixtures"]));
    return args switch
    {
        ["ingest", var path] => Ingest(path, config),
        ["ask", ..] => Ask(args, config),
        ["eval", ..] => Eval(args, config),
        ["--version"] => PrintVersion(),
        _ => Usage(),
    };

    int Ingest(string path, CsAgentConfig cfg)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(new CsAgentError(
                "ingest", "bad-args", "usage: cs-agent ingest <path>"));
            return 1;
        }

        var summary = CsAgentRuntime.IngestPath(cfg, path);
        Console.WriteLine($"ingested {summary.PagesIngested} pages ({summary.PagesResumed} resumed, {summary.PagesLoaded} found) into {summary.StorePath}");
        Console.WriteLine($"chunks embedded: {summary.ChunksEmbedded} · {summary.Seconds:F1}s · model {cfg.EmbeddingModel}");
        foreach (var skip in summary.Skipped)
            Console.WriteLine($"  skipped: {skip}");
        return 0;
    }

    int Ask(string[] askArgs, CsAgentConfig cfg)
    {
        var json = askArgs.Contains("--json");
        var question = string.Join(' ', askArgs.Skip(1).Where(a => a != "--json")).Trim('"');
        if (question.Length == 0)
        {
            Console.Error.WriteLine(new CsAgentError("ask", "bad-args", "usage: cs-agent ask [--json] \"<question>\""));
            return 1;
        }

        var isTty = Console.IsErrorRedirected == false && !json;
        var pipeline = CsAgentRuntime.CreateAskPipeline(cfg,
            progress: isTty ? stage => Console.Error.WriteLine($"… {stage}") : null);
        var result = pipeline.Run(question);

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Render(result);
        }
        return result.Resolved ? 0 : 0; // ask exit code reports errors, not the verdict
    }

    int Eval(string[] evalArgs, CsAgentConfig cfg)
    {
        var fresh = evalArgs.Contains("--fresh");
        var heldOut = evalArgs.Contains("--heldout");
        var fixturePath = heldOut ? "fixtures/questions-heldout.jsonl" : "fixtures/questions.jsonl";
        if (!File.Exists(fixturePath))
            throw new CsAgentException(new CsAgentError(
                "eval", "fixture-not-found",
                $"{fixturePath} not found — run eval from the repo root"));
        var records = FixtureLoader.FromJsonl(fixturePath);

        // ingest the fixture corpus first (idempotent), then score against the SAME store
        var corpusStorePath = string.IsNullOrWhiteSpace(cfg.StorePath) ? "./cs-agent-fixtures.db" : cfg.StorePath;
        var ingestSummary = CsAgentRuntime.IngestPath(cfg, "fixtures/docs", corpusStorePath);
        Console.WriteLine($"ingested fixtures: {ingestSummary.PagesIngested} pages ({ingestSummary.PagesResumed} resumed) into {ingestSummary.StorePath}");

        var resultsDir = heldOut ? "./eval-results/heldout" : "./eval-results";
        var isTty = Console.IsErrorRedirected == false;
        var pipeline = CsAgentRuntime.CreateAskPipeline(cfg,
            progress: isTty ? stage => Console.Error.WriteLine($"… {stage}") : null);
        var runner = new EvalRunner(q => pipeline.Run(q), resultsDir,
            progress: isTty ? q => Console.Error.WriteLine($"== {q}") : null);

        var (metrics, results, skipped) = runner.Run(records, fresh);

        Console.WriteLine();
        Console.WriteLine($"eval: {results.Count} questions ({skipped} resumed)");
        Console.WriteLine($"  resolution:       {metrics.AnswerableResolved}/{metrics.AnswerableTotal} answerable resolved · {metrics.UnanswerableEscalated}/{metrics.UnanswerableTotal} unanswerable escalated");
        Console.WriteLine($"  overall rate:     {metrics.ResolutionRate:P0}");
        Console.WriteLine($"  citation Jaccard: {metrics.CitationAccuracy:F2} (answered, page-level)");
        Console.WriteLine($"  hallucination proxy: {metrics.HallucinationProxy} zero-overlap resolutions");
        foreach (var r in results.Where(r => !r.Match))
            Console.WriteLine($"  MISS  [{(r.ExpectedOutcome == "resolved" ? "should resolve" : "should escalate")}] {r.Question}");

        var passed = EvalScoring.Passes(metrics);
        var set = heldOut ? "held-out" : "tuning";
        Console.WriteLine(passed
            ? $"PASS  {set} set: targets met (≥80% answerable, {metrics.UnanswerableEscalated}/{metrics.UnanswerableTotal} unanswerable, proxy 0)"
            : $"FAIL  {set} set: targets not met — exit 1 (honest-fail gate)");
        return passed ? 0 : 1;
    }

    static void Render(AskResult r)
    {
        var supported = r.Claims.Count(c => c.Supported);
        if (r.Resolved)
        {
            Console.WriteLine($"✓ resolved ({r.Claims.Count} claims, {supported} supported)");
            Console.WriteLine();
            Console.WriteLine(r.Answer);
            Console.WriteLine();
            Console.WriteLine("Sources:");
            foreach (var c in r.CitedChunks.Where(c => r.Claims.Any(cl => cl.SupportingChunkIds.Contains(c.Number))))
                Console.WriteLine($"[{c.Number}] {c.PagePath} — {c.Text.Split('\n').First()[..Math.Min(60, c.Text.Split('\n').First().Length)]}");
        }
        else
        {
            Console.WriteLine("✗ escalate — cannot answer from ingested docs");
            if (r.Claims.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Claims:");
                foreach (var c in r.Claims)
                {
                    var marker = c.SupportingChunkIds.Length > 0 ? c.SupportingChunkIds[0].ToString() : "-";
                    Console.WriteLine($"[{marker}] {((c.Supported ? '✓' : '✗'))} \"{c.Claim}\"");
                }
            }
            Console.WriteLine();
            Console.WriteLine("Missing:");
            foreach (var m in (r.Missing ?? []))
                Console.WriteLine($"[1] \"{m.Claim}\" — {m.Note}");
        }
        Console.WriteLine();
        Console.WriteLine($"{r.Calls} model calls · {r.Seconds:F1}s · {r.CostEstimate}");
    }

    int PrintVersion()
    {
        Console.WriteLine("cs-agent 0.1.0");
        return 0;
    }

    int Usage()
    {
        Console.Error.WriteLine("usage: cs-agent <ingest|ask|eval [--heldout] [--fresh]> ...");
        return 1;
    }
}
catch (CsAgentException ex)
{
    Console.Error.WriteLine(ex.Error.ToString());
    return 1;
}