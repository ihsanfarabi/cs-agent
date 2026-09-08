# Ingest Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill ingest-time corpus garbage — non-English crawled pages skipped at parse, duplicate page identities (canonical/trailing-slash/index.html) deduped at crawl — with zero model calls and zero Core pipeline changes.

**Architecture:** Two attribute reads added to the existing AngleSharp parse in `HtmlCrawler.ParsePage` (`<html lang>`, `<link rel="canonical">`), one URL-identity helper (`NormalizeIdentity`) shared by the crawl frontier key and the canonical dedupe, and two skip rules in `CrawlAsync` copying the existing robots/noindex skip shape into the existing `Skipped` list.

**Tech Stack:** .NET 10, C#, AngleSharp (already a dependency), xUnit. `CrawlServer` in-process Kestrel test harness already exists.

**Spec:** `docs/designs/design-2026-09-08-ingest-hygiene.md` (committed a317fd6)

## Global Constraints

- Env contract frozen — NO new environment variables. English-only crawl is a documented v1 cut, no override.
- Zero model calls added; call count only goes down (skipped pages never embed).
- `Retriever`, `SqliteVectorStore`, `Chunker`, prompts, verifier, `CsAgentConfig` — untouched. Only `HtmlCrawler.cs` changes in src.
- Skip messages follow the existing `{url} — reason` shape and land in the existing `Skipped` list that the ingest summary prints.
- Eval fixtures (`fixtures/docs`) are local-path, not crawled — eval gates untouched by construction. Full suite (151 tests) must stay green.
- TDD: failing test first, watched fail, minimal code, then full suite.
- No auto-commits — commit steps below say the message; the user gates release (v0.6.0 tag).
- RTK prefix on all shell commands (`rtk git add`, `rtk test dotnet test …`).

---

### Task 1: `ParsePage` gains Lang + Canonical fields

**Files:**
- Modify: `src/CsAgent.Core/HtmlCrawler.cs` (record `CrawledPage` line ~13, `ParsePage` lines ~158-167)
- Test: `tests/CsAgent.Tests/HtmlCrawlerTests.cs`

**Interfaces:**
- Consumes: `HtmlCrawler.ParsePage(string html)` → `Task<CrawledPage>` (exists, internal seam).
- Produces: `public sealed record CrawledPage(string Markdown, bool NoIndex, string? Lang, Uri? Canonical);` — Task 3 consumes `Lang` and `Canonical`.

- [ ] **Step 1: Write the failing tests** — append to `HtmlCrawlerTests.cs`:

```csharp
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
        // relative canonical resolves against the page URL (signature gains baseUrl)
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
        Assert.Null(offHost.Canonical); // off-host canonical NOT trusted — do not hand identity to another site

        var absent = await HtmlCrawler.ParsePage("""<html><body><main>x</main></body></html>""", baseUri);
        Assert.Null(absent.Canonical);
    }
```

The `ParsePage` signature changes to `ParsePage(string html, Uri baseUrl)` — a relative canonical needs a base. Update the 3 existing `ParsePage`-only test calls to pass any http(s) base, and the `CrawlAsync` call site to pass `finalUrl`.

- [ ] **Step 2: Run to verify failure**

Run: `rtk test dotnet test tests/CsAgent.Tests/CsAgent.Tests.csproj --filter "FullyQualifiedName~HtmlCrawlerTests"`
Expected: COMPILE ERROR — `CrawledPage` has no `Lang`/`Canonical` members.

- [ ] **Step 3: Minimal implementation** — in `HtmlCrawler.cs`:

```csharp
public sealed record CrawledPage(string Markdown, bool NoIndex, string? Lang, Uri? Canonical);

    internal static async Task<CrawledPage> ParsePage(string html, Uri baseUrl)
    {
        var document = await ParseAsync(html);
        var noIndex = document.QuerySelector("meta[name='robots']")?.GetAttribute("content")
            ?.Split(',').Any(t => t.Trim().Equals("noindex", StringComparison.OrdinalIgnoreCase))
            ?? false;
        var lang = document.DocumentElement.HasAttribute("lang")
            ? document.DocumentElement.GetAttribute("lang") : null;
        lang = string.IsNullOrWhiteSpace(lang) ? null : lang;
        Uri? canonical = null;
        var canonicalHref = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href");
        if (canonicalHref is not null && Uri.TryCreate(canonicalHref, UriKind.RelativeOrAbsolute, out var canonicalUri))
        {
            Uri? absolute = null;
            if (canonicalUri.IsAbsoluteUri) absolute = canonicalUri;
            else { try { absolute = new Uri(baseUrl, canonicalUri); } catch (UriFormatException) { } }
            if (absolute is not null && absolute.Host == baseUrl.Host)
                canonical = new Uri(absolute.GetLeftPart(UriPartial.Path)); // same normalization as NormalizeLink: query+fragment stripped
        }
        var main = document.QuerySelector("main") ?? document.QuerySelector("article")
            ?? document.QuerySelector("div[role='main']") ?? document.Body!;
        return new CrawledPage(ToMarkdown.Convert(main.InnerHtml).Trim(), noIndex, lang, canonical);
    }
```

Update `CrawlAsync` call site: `var crawled = await ParsePage(html, finalUrl);` and the two existing `ParsePage`-only test calls to pass any http(s) base URI. AngleSharp: `document.DocumentElement` is the `<html>` node; `HasAttribute` guards pages whose root lacks it.

- [ ] **Step 4: Run to verify pass** — same filter command. Expected: all HtmlCrawlerTests green.

- [ ] **Step 5: Full suite** — `rtk test dotnet test tests/CsAgent.Tests/CsAgent.Tests.csproj`. Expected: 151 green (CrawlAsync integration tests still construct fine — the record grew, call sites updated).

- [ ] **Step 6: Commit**

```bash
rtk git add src/CsAgent.Core/HtmlCrawler.cs tests/CsAgent.Tests/HtmlCrawlerTests.cs
rtk git commit -m "feat: parse html lang + canonical link in crawler"
```

---

### Task 2: `NormalizeIdentity` URL helper

**Files:**
- Modify: `src/CsAgent.Core/HtmlCrawler.cs` (add near `NormalizeLink`, line ~147)
- Test: `tests/CsAgent.Tests/HtmlCrawlerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `internal static string NormalizeIdentity(Uri url)` — Task 3 uses it for the frontier queue key and the seen-identity set.

- [ ] **Step 1: Failing test** — append:

```csharp
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
```

- [ ] **Step 2: Run to verify failure** — same filter; Expected: COMPILE ERROR, `NormalizeIdentity` does not exist.

- [ ] **Step 3: Minimal implementation**:

```csharp
    /// <summary>
    /// Identity key for dedupe: path-only URL with trailing "/" and "/index.html"
    /// collapsed. Paths keep case (case is content on real servers). Heuristic:
    /// over-collapses only when a site serves distinct pages under slash twins,
    /// which no docusaurus-class site does — and every collapse is a recorded
    /// skip, never silent.
    /// </summary>
    internal static string NormalizeIdentity(Uri url)
    {
        var path = url.GetLeftPart(UriPartial.Path);
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            path = path[..^"index.html".Length];
        if (path.Length > 0 && path != $"{url.Scheme}://{url.Authority}/" && path.EndsWith('/'))
            path = path[..^1];
        return path;
    }
```

- [ ] **Step 4: Run to verify pass** — same filter, all green.

- [ ] **Step 5: Commit**

```bash
rtk git add src/CsAgent.Core/HtmlCrawler.cs tests/CsAgent.Tests/HtmlCrawlerTests.cs
rtk git commit -m "feat: url identity normalization for crawl dedupe"
```

---

### Task 3: Crawl skip rules + frontier dedupe

**Files:**
- Modify: `src/CsAgent.Core/HtmlCrawler.cs` (`CrawlAsync`, lines ~61-140)
- Test: `tests/CsAgent.Tests/CrawlIntegrationTests.cs`

**Interfaces:**
- Consumes: `CrawledPage.Lang`/`Canonical` from Task 1, `NormalizeIdentity` from Task 2.
- Produces: skip messages `"— non-English page (lang=…)"` and `"— duplicate of …"` in `CrawlResult.Skipped`; `CrawlServer` harness reuse.

- [ ] **Step 1: Failing integration tests** — append to `CrawlIntegrationTests.cs` (note: the harness `Page()` helper emits no `lang`; use raw HTML strings where needed):

```csharp
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
    public void CanonicalDuplicate_Skipped_TwinsCollapsed()
    {
        // /copy declares a relative canonical → /original (already ingested) — skipped.
        // Relative href: resolved against the page URL inside ParsePage (Task 1),
        // so the route body needs no server URL at construction time.
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/original'>o</a><a href='/copy'>c</a>")),
            new CrawlServer.Route("/original", Page("Original", "")),
            new CrawlServer.Route("/copy",
                "<html><head><link rel='canonical' href='/original'></head><body><main>copy</main></body></html>"));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        Assert.Equal(2, result.Fetched);
        Assert.Contains(result.Skipped, s => s.Contains("duplicate of"));
    }
```

Also add a trailing-slash twin test:

```csharp
    [Fact]
    public void FrontierDedupes_SlashTwins()
    {
        using var server = new CrawlServer(
            new CrawlServer.Route("/", Page("Home", "<a href='/x/'>a</a><a href='/x'>b</a><a href='/x/index.html'>c</a>")),
            new CrawlServer.Route("/x", Page("X", "")));

        var result = new HtmlCrawler(TimeSpan.Zero).Crawl(new Uri(server.BaseUrl + "/"));

        // the server serves one route for all three forms; only ONE fetch happens
        var xHits = server.Hits.Count(h => h.Path.StartsWith("/x"));
        Assert.Equal(1, xHits);
        Assert.Equal(2, result.Fetched);
    }
```

(Kestrel route matching: if the harness routes on exact path, route `/x` and note that `/x/` may 404 — the identity key collapses all three BEFORE the fetch, so only `/x/`'s dequeued original form is fetched. Assert against `xHits` == 1 regardless of which twin was enqueued first.)

- [ ] **Step 2: Run to verify failure** — filter `FullyQualifiedName~CrawlIntegrationTests`. Expected: the 4 new tests FAIL (no skip logic; twins fetched 3 times; zh page ingested).

- [ ] **Step 3: Implementation** — in `CrawlAsync`:

a) Frontier key: replace `queued.Add(start.ToString())` / `queued.Add(link.ToString())` with the identity key, keeping the original URL for fetching:

```csharp
        var queued = new HashSet<string>(StringComparer.Ordinal);   // identity keys
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal); // ingested page identities
        // seed:
        queued.Add(NormalizeIdentity(start));
        // enqueue:
        foreach (var link in ExtractLinks(html, finalUrl))
            if (queued.Add(NormalizeIdentity(link)))
                queue.Enqueue((link, depth + 1));
```

b) Skip rules — insert after the `NoIndex` skip block, before the markdown-empty check:

```csharp
                if (crawled.Lang is not null
                    && !crawled.Lang.Equals("en", StringComparison.OrdinalIgnoreCase)
                    && !crawled.Lang.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"{urlString} — non-English page (lang={crawled.Lang})");
                    continue;
                }
                var identity = NormalizeIdentity(crawled.Canonical ?? finalUrl);
                if (!seenIdentities.Add(identity))
                {
                    skipped.Add($"{urlString} — duplicate of {identity}");
                    continue;
                }
```

`seenIdentities` is declared next to `queued` at crawl start. The language test is case-insensitive on both exact `en` and the `en-` prefix (`en-US`, `en-GB` pass; `zh-CN`, `fr` skip).

- [ ] **Step 4: Run to verify pass** — same filter, all green including the 6 pre-existing tests (resume test unaffected: same pages both walks, same identities).

- [ ] **Step 5: Full suite** — `rtk test dotnet test tests/CsAgent.Tests/CsAgent.Tests.csproj`. Expected: 151 + ~7 new, all green. Eval gate (`EvalGateTests`) rides along.

- [ ] **Step 6: Commit**

```bash
rtk git add src/CsAgent.Core/HtmlCrawler.cs tests/CsAgent.Tests/CrawlIntegrationTests.cs
rtk git commit -m "feat: crawl skips non-english + duplicate page identities"
```

---

### Task 4: README + TODOS

**Files:**
- Modify: `README.md` (URL ingest section, ~line 87-99)
- Modify: `docs/designs/TODOS.md` (OPEN entry "draft-decline escalations")

- [ ] **Step 1: README URL-ingest section** — append to the existing paragraph block:

> The crawl is English-only: pages whose `<html lang>` attribute tags a
> non-English locale (`zh-CN`, `fr`, …) are skipped at fetch and listed in the
> ingest summary as `skipped: … non-English page`. Pages without a `lang`
> attribute are allowed (English-default assumption). Local-path ingest of
> your own non-English documents is unaffected — this filters the crawl
> surface only, and there is no env override in v1. Pages whose canonical
> link (`<link rel="canonical">`) duplicates an already-ingested page, and
> trailing-slash/`index.html` twins of the same path, are deduped the same
> way (`skipped: … duplicate of …`).

- [ ] **Step 2: TODOS entry** — update the OPEN entry's "Where this goes" paragraph: shape (a) hygiene (language filter + canonical/URL dedupe) SHIPPED 2026-09-08 (design-2026-09-08-ingest-hygiene.md); record the Task-5 measurement numbers when available; hybrid/rerank decision stays deferred behind that measurement; shape (b) unchanged (draft over-declining, revisit after retrieval clean).

- [ ] **Step 3: Commit**

```bash
rtk git add README.md docs/designs/TODOS.md
rtk git commit -m "docs: english-only crawl + dedupe limitations"
```

---

### Task 5: Live verification — fresh docusaurus crawl (the honest gate)

**Files:** none (measurement; results recorded into TODOS per Task 4 Step 2 if not already written there)

- [ ] **Step 1: Fresh crawl + ingest** (needs `CS_AGENT_MODEL_KEY` from `~/.cs-agent-key`; ~15-30 min):

```bash
source ~/.cs-agent-key
rm -f cs-agent-docs-docusaurus-io.db
rtk test dotnet run --project src/CsAgent.Cli -- ingest https://docusaurus.io 2>&1 | tail -5
```

Expected: page count BELOW the pre-hygiene 200-page cap (localized pages + duplicates gone — the cap previously truncated the crawl); `skipped:` lines include `non-English page (lang=…)` and `duplicate of …` entries.

- [ ] **Step 2: Repro questions re-asked** — the zero-claims investigation's failing shapes through the fresh store (the navbar question and the versioning question cited docusaurus.io/versions at 0.72):

```bash
rtk test dotnet run --project src/CsAgent.Cli -- ask "How do I configure the navbar in docusaurus.config.js?" 2>&1 | tail -5
```

Expected: citations never include a `/zh-CN/` (or other localized) page; the zh-CN-showcase garbage path is dead. Resolution may STILL escalate on blog-post noise for some questions — that residual is the recorded hybrid/rerank decision input, not a failure of this increment.

- [ ] **Step 3: Record numbers** — page count, skip counts, repro outcomes → TODOS entry (Task 4 Step 2 placeholder gets real numbers).

- [ ] **Step 4: Commit TODOS numbers**

```bash
rtk git add docs/designs/TODOS.md
rtk git commit -m "docs: record hygiene measurement on docusaurus corpus"
```

---

### Task 6: Release v0.6.0 (USER GATE)

- [ ] **Step 1:** Bump `src/CsAgent.Cli/CsAgent.Cli.csproj` `<Version>0.5.0</Version>` → `0.6.0`.
- [ ] **Step 2:** Full suite green.
- [ ] **Step 3:** Commit + push + tag `v0.6.0` + push tag — ONLY after the user says go (no-auto-commit convention; also the v0.5.0 tag is currently still unpushed — confirm with the user whether to ship 0.5.0 first or supersede it).
- [ ] **Step 4:** Watch workflows: ci (suite), docker build job, then on tag: publish (NuGet) + docker (GHCR cosign sign/attest — first live run of the new signing steps). Post-publish: `cosign verify ghcr.io/ihsanfarabi/cs-agent:v0.6.0 …` per the README verify line.