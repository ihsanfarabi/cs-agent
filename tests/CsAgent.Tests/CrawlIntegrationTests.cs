using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public sealed class CrawlIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-crawl-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static string Page(string title, string links) =>
        $"<html><body><main><h1>{title}</h1>{links}</main></body></html>";

    [Fact]
    public void Crawl_SameHostOnly_DepthCap()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/d1'>one</a><a href='https://example.com/x'>ext</a>")),
            new CrawlServer.Route("/d1", Page("One", "<a href='/d2'>two</a>")),
            new CrawlServer.Route("/d2", Page("Two", "")));

        var result = new HtmlCrawler(TimeSpan.Zero, maxDepth: 1).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(2, result.Fetched);                 // / and /d1; /d2 is depth 2 > cap
        Assert.Contains(result.Pages, p => p.Key == server.BaseUrl + "/");
        Assert.Contains(result.Pages, p => p.Key == server.BaseUrl + "/d1");
        Assert.DoesNotContain(server.Hits, h => h.Path == "/d2");       // never requested
        Assert.DoesNotContain(server.Hits, h => h.Path.Contains("example.com")); // off-host dropped pre-fetch
    }

    [Fact]
    public void NoindexPage_LinkedFromSeed_SkippedWithWarning()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/blocked'>b</a>")),
            new CrawlServer.Route("/blocked", "<html><head><meta name='robots' content='noindex'></head><body><main><h1>Hidden</h1></main></body></html>"));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(1, result.Fetched);
        Assert.Contains(result.Skipped, s => s.Contains("noindex"));
    }

    [Fact]
    public void RobotsDisallow_BlocksPaths_AbsentRobotsAllows()
    {
        var blocked = new CrawlServer(
            new CrawlServer.Route("/robots.txt",
                "User-agent: *\nDisallow: /private\n", ContentType: "text/plain"),
            new CrawlServer.Route("/", Page("Home", "<a href='/private/secret'>s</a>")),
            new CrawlServer.Route("/private/secret", Page("Secret", "")));
        using (blocked)
        {
            var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(blocked.BaseUrl + "/"));
            Assert.Equal(1, result.Fetched);
            Assert.DoesNotContain(blocked.Hits, h => h.Path == "/private/secret");
            Assert.Contains(result.Skipped, s => s.Contains("robots.txt Disallow"));
        }

        // no robots route served → Kestrel 404 → allow-all
        using var open = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/open'>o</a>")),
            new CrawlServer.Route("/open", Page("Open", "")));
        var allowed = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(open.BaseUrl + "/"));
        Assert.Equal(2, allowed.Fetched);
    }

    [Fact]
    public void RobotsUnreachable_FailsClosed_NoPageFetches()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/robots.txt", "", Status: 500),
            new CrawlServer.Route("/", Page("Home", "")));

        var ex = Assert.Throws<CsAgentException>(
            () => new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/")));
        Assert.Equal("crawl-robots-unreachable", ex.Error.Code);
        Assert.Contains(server.Hits, h => h.Path == "/robots.txt");
        Assert.DoesNotContain(server.Hits, h => h.Path == "/"); // zero page fetches
    }

    [Fact]
    public void Crawl_RespectsPageCapAndInterval()
    {
        var links = string.Concat(Enumerable.Range(0, 5).Select(i => $"<a href='/p{i}'>p{i}</a>"));
        var routes = new List<CrawlServer.Route> { new("/", Page("Home", links)) };
        for (var i = 0; i < 5; i++)
            routes.Add(new CrawlServer.Route($"/p{i}", Page($"P{i}", "")));
        using var server = new CrawlServer(routes.ToArray());

        var result = new HtmlCrawler(TimeSpan.FromMilliseconds(200), maxPages: 3)
            .Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(3, result.Fetched);
        Assert.Contains(result.Skipped, s => s.Contains("page cap"));
        var pageHits = server.Hits
            .Where(h => h.Path != "/robots.txt")
            .OrderBy(h => h.At)
            .Select(h => h.At)
            .ToList();
        for (var i = 1; i < pageHits.Count; i++)
            Assert.True(pageHits[i] - pageHits[i - 1] >= TimeSpan.FromMilliseconds(150),
                $"interval gap {pageHits[i] - pageHits[i - 1]}");
    }

    [Fact]
    public void Crawl_Reproducible_PipelineResumesWithoutReembedding()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/a'>a</a>")),
            new CrawlServer.Route("/a", Page("A", "")));

        // D8: resume is pipeline-level — the crawl re-walks (same pages both
        // times), and the second ingest resumes every page by content hash
        var first = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));
        var second = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));
        Assert.Equal(first.Pages.Select(p => p.Key).OrderBy(k => k),
            second.Pages.Select(p => p.Key).OrderBy(k => k));

        var embeddings = new FakeEmbeddingGenerator();
        using (var store = new SqliteVectorStore(_dbPath, "fake-embedding"))
        {
            new IngestPipeline(embeddings, store, chunkSize: 1200, chunkOverlap: 150)
                .Run(new LoadReport(first.Pages, first.Skipped), "crawl-test");
        }
        using (var store = new SqliteVectorStore(_dbPath, "fake-embedding"))
        {
            var summary = new IngestPipeline(embeddings, store, chunkSize: 1200, chunkOverlap: 150)
                .Run(new LoadReport(second.Pages, second.Skipped), "crawl-test");
            Assert.Equal(2, summary.PagesResumed);
            Assert.Equal(0, summary.PagesIngested);
            Assert.Equal(0, summary.ChunksEmbedded);
        }
    }

    [Fact]
    public void CrawledPages_IngestThenAsk_CitesUrlPagePaths()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Keys", "<p>Rotate keys from Settings then API keys then Rotate.</p>")));

        var crawl = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));
        var embeddings = new FakeEmbeddingGenerator();
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        new IngestPipeline(embeddings, store, chunkSize: 1200, chunkOverlap: 150)
            .Run(new LoadReport(crawl.Pages, crawl.Skipped), "crawl-test");

        var retriever = new Retriever(embeddings, new SqliteVectorStore(_dbPath, "fake-embedding"), topK: 5);
        var draftClient = new FakeChatClient(_ => "Rotate keys from Settings. [1]");
        var verifyJson = """[{"claim":"Rotate keys from Settings","supported":true,"supporting_chunk_ids":[1]}]""";
        var verifyClient = new FakeChatClient(_ => verifyJson);
        Microsoft.Agents.AI.ChatClientAgent Draft() => new(draftClient);
        Microsoft.Agents.AI.ChatClientAgent Verify() => new(verifyClient);
        var result = new AskPipeline(Draft, Verify, retriever).Run("How do I rotate keys?");

        Assert.True(result.Resolved);
        Assert.All(result.CitedChunks, c => Assert.StartsWith(server.BaseUrl + "/", c.PagePath));
    }

    // --- ingest hygiene (design-2026-09-08-ingest-hygiene.md) ---

    [Fact]
    public void NonEnglishPage_SkippedWithReason()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/zh'>z</a>")),
            new CrawlServer.Route("/zh", "<html lang='zh-CN'><body><main><h1>Chinese page</h1></main></body></html>"));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(1, result.Fetched);
        Assert.Single(result.Pages);
        Assert.Contains(result.Skipped, s => s.Contains("non-English page") && s.Contains("lang=zh-CN"));
    }

    [Fact]
    public void EnglishAndUntaggedPages_Pass()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", "<html lang='en'><body><main><a href='/en-us'>e</a><a href='/plain'>p</a></main></body></html>"),
            new CrawlServer.Route("/en-us", "<html lang='en-US'><body><main>en-us page</main></body></html>"),
            new CrawlServer.Route("/plain", "<html><body><main>untagged page</main></body></html>"));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(3, result.Fetched);
        Assert.DoesNotContain(result.Skipped, s => s.Contains("non-English"));
    }

    [Fact]
    public void CanonicalDuplicate_Skipped()
    {
        // /copy declares a relative canonical → /original (already ingested) — skipped.
        // Relative href: resolved against the page URL inside ParsePage, so the
        // route body needs no server URL at construction time.
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/original'>o</a><a href='/copy'>c</a>")),
            new CrawlServer.Route("/original", Page("Original", "")),
            new CrawlServer.Route("/copy",
                "<html><head><link rel='canonical' href='/original'></head><body><main>copy</main></body></html>"));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(2, result.Fetched);
        Assert.Contains(result.Skipped, s => s.Contains("duplicate of"));
    }

    [Fact]
    public void FrontierDedupes_SlashTwins()
    {
        // three hrefs, one identity (/x, /x/, /x/index.html collapse in the
        // queue key) — only one fetch happens
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/x/'>a</a><a href='/x'>b</a><a href='/x/index.html'>c</a>")),
            new CrawlServer.Route("/x", Page("X", "")));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        var xHits = server.Hits.Count(h => h.Path.StartsWith("/x"));
        Assert.Equal(1, xHits);
        Assert.Equal(2, result.Fetched);
    }
}