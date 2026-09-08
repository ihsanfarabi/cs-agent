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
    public void NormalizeIdentity_CollapsesSlashTwins()
    {
        string Id(string url) => HtmlCrawler.NormalizeIdentity(new Uri(url));
        Assert.Equal(Id("https://docs.foo.com/docs/foo"), Id("https://docs.foo.com/docs/foo/"));
        Assert.Equal(Id("https://docs.foo.com/docs/foo"), Id("https://docs.foo.com/docs/foo/index.html"));
        Assert.Equal(Id("https://docs.foo.com/"), Id("https://docs.foo.com")); // root twins too
        Assert.NotEqual(Id("https://docs.foo.com/docs/foo"), Id("https://docs.foo.com/docs/bar"));
        Assert.NotEqual(Id("https://docs.foo.com/Docs"), Id("https://docs.foo.com/docs")); // case is content: no case-folding
        Assert.Equal("https://docs.foo.com/docs/foo", Id("https://docs.foo.com/docs/foo/index.html"));
    }

    [Fact]
    public async Task ParsePage_SelectsMain_KeepsHeadings()
    {
        var page = await HtmlCrawler.ParsePage(
            """<html><body><main><h1>Title</h1><p>Body text</p></main><footer>chrome</footer></body></html>""",
            new Uri("https://docs.foo.com/"));
        Assert.False(page.NoIndex);
        Assert.Contains("# Title", page.Markdown);
        Assert.Contains("Body text", page.Markdown);
        Assert.DoesNotContain("chrome", page.Markdown);
    }

    [Fact]
    public async Task ParsePage_FallsBackArticleThenBody()
    {
        var article = await HtmlCrawler.ParsePage(
            """<html><body><article><h1>A</h1></article></body></html>""",
            new Uri("https://docs.foo.com/"));
        Assert.Contains("# A", article.Markdown);

        var body = await HtmlCrawler.ParsePage(
            """<html><body><h1>B</h1><nav>navtext</nav></body></html>""",
            new Uri("https://docs.foo.com/"));
        Assert.Contains("# B", body.Markdown);
        Assert.Contains("navtext", body.Markdown); // body fallback keeps chrome — documented
    }

    [Fact]
    public async Task ParsePage_NoIndexDetected()
    {
        var blocked = await HtmlCrawler.ParsePage(
            """<html><head><meta name="robots" content="noindex, nofollow"></head><body><main>x</main></body></html>""",
            new Uri("https://docs.foo.com/"));
        Assert.True(blocked.NoIndex);
        var allowed = await HtmlCrawler.ParsePage(
            """<html><head><meta name="robots" content="index, follow"></head><body><main>x</main></body></html>""",
            new Uri("https://docs.foo.com/"));
        Assert.False(allowed.NoIndex);
    }

    [Fact]
    public async Task ParsePage_LangAttribute_Read()
    {
        var baseUri = new Uri("https://docs.foo.com/");
        var en = await HtmlCrawler.ParsePage("""<html lang="en"><body><main>x</main></body></html>""", baseUri);
        Assert.Equal("en", en.Lang);
        var enUs = await HtmlCrawler.ParsePage("""<html lang="en-US"><body><main>x</main></body></html>""", baseUri);
        Assert.Equal("en-US", enUs.Lang);
        var zh = await HtmlCrawler.ParsePage("""<html lang="zh-CN"><body><main>x</main></body></html>""", baseUri);
        Assert.Equal("zh-CN", zh.Lang); // field reports; the skip decision is the crawl loop's
        var absent = await HtmlCrawler.ParsePage("""<html><body><main>x</main></body></html>""", baseUri);
        Assert.Null(absent.Lang); // absent = allowed (English-default assumption)
    }

    [Fact]
    public async Task ParsePage_CanonicalLink_ResolvedOrOffHostNull()
    {
        var baseUri = new Uri("https://docs.foo.com/elsewhere");
        // relative canonical resolves against the page URL
        var withCanonical = await HtmlCrawler.ParsePage(
            """<html><head><link rel="canonical" href="/docs/foo"></head><body><main>x</main></body></html>""",
            baseUri);
        Assert.NotNull(withCanonical.Canonical);
        Assert.Equal("https://docs.foo.com/docs/foo", withCanonical.Canonical!.ToString());

        var absolute = await HtmlCrawler.ParsePage(
            """<html><head><link rel="canonical" href="https://docs.foo.com/real"></head><body><main>x</main></body></html>""",
            baseUri);
        Assert.Equal("https://docs.foo.com/real", absolute.Canonical!.ToString());

        var offHost = await HtmlCrawler.ParsePage(
            """<html><head><link rel="canonical" href="https://evil.com/hijack"></head><body><main>x</main></body></html>""",
            baseUri);
        Assert.Null(offHost.Canonical); // off-host canonical NOT trusted — identity stays with the page's own host

        var absent = await HtmlCrawler.ParsePage("""<html><body><main>x</main></body></html>""", baseUri);
        Assert.Null(absent.Canonical);
    }
}