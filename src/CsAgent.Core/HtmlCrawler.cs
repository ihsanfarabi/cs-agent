using System.Net;
using System.Net.Http;
using AngleSharp;
using AngleSharp.Dom;

namespace CsAgent.Core;

public sealed record CrawlResult(
    IReadOnlyList<LoadedPage> Pages,
    IReadOnlyList<string> Skipped,
    int Fetched);

public sealed record CrawledPage(string Markdown, bool NoIndex);

/// <summary>
/// Same-domain HTML crawl: fetch with HttpClient, DOM-parse with AngleSharp
/// (no AngleSharp.Io), main content → markdown via ReverseMarkdown (default
/// config). Fixed limits: ≤ MaxPages pages, depth ≤ MaxDepth (seed = 0),
/// ≥ CrawlMinInterval between page requests (a larger robots Crawl-delay
/// wins; robots itself is fetched once and does not count). robots 5xx or
/// unreachable fails closed — zero page fetches. Constructor takes overrides
/// for tests only; production always uses the constants.
///
/// Resume is PIPELINE-level (D8, 2026-09-06): a re-crawl re-fetches HTML
/// (links must be re-walked to rebuild the frontier) and IngestPipeline's
/// content-hash check skips re-embedding unchanged pages — same pattern as
/// path mode.
/// </summary>
public sealed class HtmlCrawler
{
    public static readonly TimeSpan CrawlMinInterval = TimeSpan.FromSeconds(1);
    internal const int MaxPages = 200;
    internal const int MaxDepth = 3;

    private static readonly ReverseMarkdown.Converter ToMarkdown = new();
    private static readonly HttpClient Http = CreateClient();

    private readonly TimeSpan _minInterval;
    private readonly int _maxDepth;
    private readonly int _maxPages;

    public HtmlCrawler(TimeSpan? minInterval = null, int maxDepth = MaxDepth, int maxPages = MaxPages)
    {
        _minInterval = minInterval ?? CrawlMinInterval;
        _maxDepth = maxDepth;
        _maxPages = maxPages;
    }

    public CrawlResult Crawl(Uri seed, Action<string>? progress = null) =>
        CrawlAsync(seed, progress).GetAwaiter().GetResult();

    public async Task<CrawlResult> CrawlAsync(Uri seed, Action<string>? progress = null)
    {
        if (seed.Scheme is not ("http" or "https"))
            throw new CsAgentException(new CsAgentError("crawl", "bad-url",
                $"Crawl seed must be http(s): '{seed}'."));
        var host = seed.Host;
        var rules = await FetchRobotsAsync(seed);
        var interval = rules.CrawlDelay is { } delay && delay > _minInterval ? delay : _minInterval;

        var pages = new List<LoadedPage>();
        var skipped = new List<string>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Uri Url, int Depth)>();
        var start = new Uri(seed.GetLeftPart(UriPartial.Path)); // fragment+query stripped
        queued.Add(start.ToString());
        queue.Enqueue((start, 0));

        var fetched = 0;
        var lastRequest = DateTimeOffset.MinValue;

        while (queue.Count > 0 && fetched < _maxPages)
        {
            var (url, depth) = queue.Dequeue();
            var urlString = url.ToString();

            if (!rules.IsAllowed(url.AbsolutePath))
            {
                skipped.Add($"{urlString} — robots.txt Disallow");
                continue;
            }

            progress?.Invoke(urlString);
            if (lastRequest != DateTimeOffset.MinValue)
            {
                var wait = interval - (DateTimeOffset.UtcNow - lastRequest);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
            }
            lastRequest = DateTimeOffset.UtcNow;

            try
            {
                using var response = await Http.GetAsync(url);
                var finalUrl = response.RequestMessage!.RequestUri!;
                if (finalUrl.Host != host)
                {
                    skipped.Add($"{urlString} — redirected off-host to {finalUrl.Host}");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    skipped.Add($"{urlString} — HTTP {(int)response.StatusCode}");
                    continue;
                }
                if (response.Content.Headers.ContentType?.MediaType != "text/html")
                {
                    skipped.Add($"{urlString} — not HTML ({response.Content.Headers.ContentType?.MediaType ?? "unknown"})");
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync();
                var crawled = await ParsePage(html);
                if (crawled.NoIndex)
                {
                    skipped.Add($"{urlString} — meta robots noindex");
                    continue;
                }
                if (crawled.Markdown.Length == 0)
                {
                    skipped.Add($"{urlString} — no extractable content");
                    continue;
                }

                pages.Add(new LoadedPage(finalUrl.ToString(), crawled.Markdown));
                fetched++;

                // links found on depth-max pages are discovered but never fetched
                if (depth < _maxDepth)
                    foreach (var link in ExtractLinks(html, finalUrl))
                        if (queued.Add(link.ToString()))
                            queue.Enqueue((link, depth + 1));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                skipped.Add($"{urlString} — fetch failed ({ex.Message})");
            }
        }

        if (fetched == _maxPages && queue.Count > 0)
            skipped.Add($"crawl stopped at {_maxPages}-page cap ({queue.Count} URLs not fetched)");

        return new CrawlResult(pages, skipped, fetched);
    }

    internal static string CorpusNameFromHost(string host) => host.Replace('.', '-');

    internal static Uri? NormalizeLink(Uri baseUrl, string href)
    {
        if (!Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var relative)) return null;
        Uri absolute;
        try { absolute = new Uri(baseUrl, relative); }
        catch (UriFormatException) { return null; }
        if (absolute.Scheme is not ("http" or "https")) return null; // mailto:, javascript:, tel:
        if (absolute.Host != baseUrl.Host) return null;                // same-host exact match
        return new Uri(absolute.GetLeftPart(UriPartial.Path));          // strips query + fragment
    }

    internal static async Task<CrawledPage> ParsePage(string html)
    {
        var document = await ParseAsync(html);
        var noIndex = document.QuerySelector("meta[name='robots']")?.GetAttribute("content")
            ?.Split(',').Any(t => t.Trim().Equals("noindex", StringComparison.OrdinalIgnoreCase))
            ?? false;
        var main = document.QuerySelector("main") ?? document.QuerySelector("article")
            ?? document.QuerySelector("div[role='main']") ?? document.Body!;
        return new CrawledPage(ToMarkdown.Convert(main.InnerHtml).Trim(), noIndex);
    }

    private static IEnumerable<Uri> ExtractLinks(string html, Uri baseUrl)
    {
        var document = ParseAsync(html).GetAwaiter().GetResult();
        return document.QuerySelectorAll("a[href]")
            .Select(a => NormalizeLink(baseUrl, a.GetAttribute("href")!))
            .Where(u => u is not null)
            .Select(u => u!);
    }

    private static async Task<IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New();
        return await context.OpenAsync(request => request.Content(html));
    }

    private static async Task<RobotsRules> FetchRobotsAsync(Uri seed)
    {
        var robotsUrl = new Uri($"{seed.Scheme}://{seed.Authority}/robots.txt");
        try
        {
            using var response = await Http.GetAsync(robotsUrl);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return RobotsRules.Parse(""); // absent robots = allow-all
            if (!response.IsSuccessStatusCode)
                throw new CsAgentException(new CsAgentError("crawl", "crawl-robots-unreachable",
                    $"robots.txt at {robotsUrl} returned HTTP {(int)response.StatusCode} — failing closed, no pages fetched."));
            return RobotsRules.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            throw new CsAgentException(new CsAgentError("crawl", "crawl-robots-unreachable",
                $"robots.txt at {robotsUrl} unreachable ({ex.Message}) — failing closed, no pages fetched."));
        }
    }

    private static HttpClient CreateClient()
    {
        var version = typeof(HtmlCrawler).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"cs-agent/{version} (+https://github.com/ihsanfarabi/cs-agent)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}