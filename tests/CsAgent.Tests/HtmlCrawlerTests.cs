using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public sealed class HtmlCrawlerTests
{
    [Fact]
    public void NormalizeLink_ResolvesRelative_StripsFragmentAndQuery()
    {
        var baseUri = new Uri("https://docs.foo.com/guides/start");
        var link = HtmlCrawler.NormalizeLink(baseUri, "../api/keys?utm=1#top");
        Assert.Equal("https://docs.foo.com/api/keys", link!.ToString());
        Assert.Equal("https://docs.foo.com/flat", HtmlCrawler.NormalizeLink(baseUri, "/flat#frag")!.ToString());
    }

    [Fact]
    public void NormalizeLink_OffHostOrNonHttp_YieldsNull()
    {
        var baseUri = new Uri("https://docs.foo.com/");
        Assert.Null(HtmlCrawler.NormalizeLink(baseUri, "https://example.com/x"));
        Assert.Null(HtmlCrawler.NormalizeLink(baseUri, "mailto:a@b.c"));
        Assert.Null(HtmlCrawler.NormalizeLink(baseUri, "javascript:void(0)"));
        Assert.NotNull(HtmlCrawler.NormalizeLink(baseUri, "/same-host"));
    }

    [Fact]
    public void CorpusNameFromHost_DotsBecomeDashes()
    {
        Assert.Equal("docs-foo-com", HtmlCrawler.CorpusNameFromHost("docs.foo.com"));
        Assert.Equal("foo-com", HtmlCrawler.CorpusNameFromHost("foo.com"));
    }

    [Fact]
    public async Task ParsePage_SelectsMain_KeepsHeadings()
    {
        var page = await HtmlCrawler.ParsePage(
            """<html><body><main><h1>Title</h1><p>Body text</p></main><footer>chrome</footer></body></html>""");
        Assert.False(page.NoIndex);
        Assert.Contains("# Title", page.Markdown);
        Assert.Contains("Body text", page.Markdown);
        Assert.DoesNotContain("chrome", page.Markdown);
    }

    [Fact]
    public async Task ParsePage_FallsBackArticleThenBody()
    {
        var article = await HtmlCrawler.ParsePage(
            """<html><body><article><h1>A</h1></article></body></html>""");
        Assert.Contains("# A", article.Markdown);

        var body = await HtmlCrawler.ParsePage(
            """<html><body><h1>B</h1><nav>navtext</nav></body></html>""");
        Assert.Contains("# B", body.Markdown);
        Assert.Contains("navtext", body.Markdown); // body fallback keeps chrome — documented
    }

    [Fact]
    public async Task ParsePage_NoIndexDetected()
    {
        var blocked = await HtmlCrawler.ParsePage(
            """<html><head><meta name="robots" content="noindex, nofollow"></head><body><main>x</main></body></html>""");
        Assert.True(blocked.NoIndex);
        var allowed = await HtmlCrawler.ParsePage(
            """<html><head><meta name="robots" content="index, follow"></head><body><main>x</main></body></html>""");
        Assert.False(allowed.NoIndex);
    }
}