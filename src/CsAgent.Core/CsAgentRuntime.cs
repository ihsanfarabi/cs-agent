using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CsAgent.Core;

/// <summary>
/// Provider wiring shared by all surfaces (CLI, MCP, eval): env config →
/// ingest path or ask pipeline. The ONE result object comes from AskPipeline;
/// no surface builds its own copy.
/// </summary>
public static class CsAgentRuntime
{
    public static AskPipeline CreateAskPipeline(CsAgentConfig cfg, Action<string>? progress = null)
    {
        var storePath = CorpusPointer.ResolveStorePath(cfg.StorePath);
        var client = BuildClient(cfg);
        IEmbeddingGenerator<string, Embedding<float>> embeddings =
            client.GetEmbeddingClient(cfg.EmbeddingModel).AsIEmbeddingGenerator();
        var store = new SqliteVectorStore(storePath, cfg.EmbeddingModel);
        var retriever = new Retriever(embeddings, store, cfg.TopK);

        AIAgent Draft() => DeterministicAgent(client, cfg.DraftModel, "Draft", Prompts.DraftInstructions());
        AIAgent Verify() => DeterministicAgent(client, cfg.VerifyModel, "Verify",
            "You output ONLY the JSON claims array described in the prompt.");

        return new AskPipeline(Draft, Verify, retriever, progress);
    }

    public static IngestSummary IngestPath(CsAgentConfig cfg, string path, string? storePath = null)
    {
        var resolvedStore = storePath
            ?? (string.IsNullOrWhiteSpace(cfg.StorePath)
                ? $"./cs-agent-{new DirectoryInfo(path).Name}.db"
                : cfg.StorePath);
        var client = BuildClient(cfg);
        IEmbeddingGenerator<string, Embedding<float>> embeddings =
            client.GetEmbeddingClient(cfg.EmbeddingModel).AsIEmbeddingGenerator();
        using var store = new SqliteVectorStore(resolvedStore, cfg.EmbeddingModel);
        var pipeline = new IngestPipeline(embeddings, store, cfg.ChunkSize, cfg.ChunkOverlap);
        var summary = pipeline.Run(path, new DirectoryInfo(path).Name) with { StorePath = resolvedStore };
        CorpusPointer.Update(resolvedStore);
        return summary;
    }

    public static IngestSummary IngestUrl(CsAgentConfig cfg, string url, string? storePath = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var seed) || seed.Scheme is not ("http" or "https"))
            throw new CsAgentException(new CsAgentError("crawl", "bad-url",
                $"Ingest URL must be absolute http(s): '{url}'."));
        var corpusName = HtmlCrawler.CorpusNameFromHost(seed.Host);
        var resolvedStore = storePath
            ?? (string.IsNullOrWhiteSpace(cfg.StorePath)
                ? $"./cs-agent-{corpusName}.db"
                : cfg.StorePath);

        var client = BuildClient(cfg);
        IEmbeddingGenerator<string, Embedding<float>> embeddings =
            client.GetEmbeddingClient(cfg.EmbeddingModel).AsIEmbeddingGenerator();
        using var store = new SqliteVectorStore(resolvedStore, cfg.EmbeddingModel);
        var known = store.IngestedPaths();

        // resume semantics: ingested URLs are skipped without re-fetch — a
        // killed crawl resumes at the unvisited pages, not the whole corpus
        var crawl = new HtmlCrawler().Crawl(seed, known.Contains);
        if (crawl.Pages.Count == 0)
            throw new CsAgentException(new CsAgentError("crawl", "no-crawlable-pages",
                $"Crawled 0 pages from '{url}'. " +
                (crawl.Skipped.Count > 0
                    ? $"Skipped: {string.Join("; ", crawl.Skipped.Take(3))}"
                    : "No same-host HTML pages found.")));

        var pipeline = new IngestPipeline(embeddings, store, cfg.ChunkSize, cfg.ChunkOverlap);
        var ingested = pipeline.Run(new LoadReport(crawl.Pages, crawl.Skipped), corpusName);
        var summary = ingested with
        {
            StorePath = resolvedStore,
            PagesResumed = ingested.PagesResumed + crawl.SkippedKnown
        };
        CorpusPointer.Update(resolvedStore);
        return summary;
    }

    private static OpenAIClient BuildClient(CsAgentConfig cfg) =>
        new(new System.ClientModel.ApiKeyCredential(cfg.ModelKey),
            new OpenAIClientOptions { Endpoint = new Uri(cfg.BaseUrl) });

    /// <summary>Temperature 0 — the verdict gate must not flip on sampling variance.</summary>
    private static AIAgent DeterministicAgent(OpenAIClient client, string model, string name, string instructions) =>
        new DeterministicChatClient(client.GetChatClient(model).AsIChatClient())
            .AsAIAgent(name: name, instructions: instructions);
}