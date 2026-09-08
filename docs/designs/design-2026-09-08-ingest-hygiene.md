# Design: Ingest hygiene — crawl language filter + URL canonicalization

Date: 2026-09-08. Owner: retrieval-noise track (TODOS OPEN entry "draft-decline
escalations on crawled corpora"). Brainstormed via superpowers:brainstorming
(architectural path — corpus content contract changes); scope decisions below
approved in chat 2026-09-08.

## Problem

The zero-claims investigation (2026-09-08) live-reproduced the upstream shape
of every draft-decline escalation on the docusaurus store: **garbage
retrieval**. Top-8 for a navbar question was blog release posts, a zh-CN
showcase page, and ReverseMarkdown HTML-soup chunks (nav chrome, SVG path
data). The draft model declines when the chunks are garbage; the verifier
correctly returns `[]`; the ask escalates. Two of the garbage sources are
ingest-time problems, not retrieval-quality problems:

1. **Localized pages pollute the corpus.** The docusaurus crawl ingests every
   localized page (`/zh-CN/…`, `/fr/…`, …) — content the engine cannot use
   (English prompts, English evals) but which embeds into the vector space
   and gets retrieved. These pages burn embed calls and retrieval slots.
2. **Duplicate page identities.** The same page is reachable under multiple
   URLs (trailing-slash twins, `index.html` variants, canonical-tagged
   mirrors). Each variant ingests as a separate page — duplicate chunks
   compete for retrieval slots (MMR mitigates clone twins *within* a page
   but cannot dedupe whole-page copies across identities).

Blog release posts and nav-chrome questions are **legit pages** — hygiene
does not and should not remove them. That residual retrieval noise is the
hybrid/rerank decision, which stays deferred behind the post-hygiene
measurement (this doc's Success criteria).

## Approach (approved in chat)

Crawl-path-only hygiene; three small mechanisms in `HtmlCrawler`, zero model
calls, zero Core pipeline changes, env contract frozen (no new vars —
English-only is a documented v1 cut):

1. **Language filter** — after the existing AngleSharp parse, read the
   `<html lang>` attribute. Present + not an `en`/`en-*` prefix → skip the
   page, recorded in the existing `Skipped` list. Absent `lang` → allowed
   (English-default assumption). Local file ingest is untouched — users
   ingesting their own non-English docs keep full behavior; the crawl is the
   only surface this filters.
2. **Frontier dedupe on normalized URLs** — collapse trailing slash and
   `index.html` in the queue key so `/docs/foo`, `/docs/foo/`, and
   `/docs/foo/index.html` are one queue entry, not three fetches.
3. **Canonical identity dedupe** — after parse, `<link rel="canonical">`
   (resolved absolute, same normalization) is the page identity when
   present, else the final URL. An identity already ingested this crawl →
   skip + record. No redirect-chain chasing, no sitemap seeding (explicitly
   rejected as scope creep in brainstorm).

Net effect: fewer pages fetched (queue dedupe), fewer pages ingested (lang +
canonical), fewer embed calls. Corpus for the flagship docusaurus demo gets
substantially cleaner.

## Design detail

### `CrawledPage` record (breaking internal change)

```csharp
public sealed record CrawledPage(string Markdown, bool NoIndex, string? Lang, Uri? Canonical);
```

`ParsePage` already DOM-parses every page — it gains two reads:

- `Lang`: `document.DocumentElement.GetAttribute("lang")` (the `<html>` tag),
  null when absent/empty.
- `Canonical`: `link[rel='canonical']` href, resolved against the page URL
  via `NormalizeLink`-style same-host rules, null when absent or off-host.

`ParsePage` is an `internal static` seam with existing test coverage — both
new fields get direct tests through it.

### Skip rules in `CrawlAsync` (insert after the noindex check, in order)

```
if lang present && !lang startsWith "en-"-or-equals-"en" (case-insensitive)
    skipped.Add($"{urlString} — non-English page (lang={lang})"); continue;
identity = canonical ?? finalUrl, normalized (trailing slash + /index.html collapsed)
if !seenIdentities.Add(identity)
    skipped.Add($"{urlString} — duplicate of {identity}"); continue;
```

Case-insensitive prefix match: `en`, `EN`, `en-US`, `en-GB` pass; `zh-CN`,
`fr`, `pt-BR` skip. The skip messages follow the existing `{url} — reason`
shape the ingest summary already prints.

### Frontier normalization

`NormalizeLink` keeps its signature and postcondition (path-only same-host
URL) but the **queue key** additionally collapses a trailing `/` and a
trailing `/index.html` (via one `NormalizeIdentity(Uri)` helper shared with
the canonical check). `queued` tracks the normalized key; the dequeued URL
keeps its original form for fetching (server may treat them differently —
the fetch must be of a real URL; only the identity is collapsed).

`NormalizeIdentity(Uri)` — `internal static`, directly tested: paths keep
their case (case is content on real servers); only the two suffix collapses
apply. It is an identity heuristic: it may over-collapse two genuinely
different pages only when a site serves distinct content at `/foo` and
`/foo/`, which no docusaurus-class site does.

### What does NOT change

- `RobotsRules`, pacing, page cap, depth, noindex, content-type checks,
  resume mechanism (content-hash skip at the IngestPipeline level).
- `Retriever`, `SqliteVectorStore`, `Chunker`, prompts, verifier — zero
  Core retrieval/pipeline changes.
- Env vars, result object, cost model (call count only goes DOWN).
- Eval gates: `fixtures/docs` is a local-path corpus — crawl hygiene cannot
  touch it. `EvalGateTests` stub gate unaffected by construction.

### Existing stores

Hygiene is ingest-time only. A store ingested before this change keeps its
localized pages — a resume re-walk will now *skip fetching* them, but
already-ingested pages stay until re-ingested from a fresh store. The
flagship docusaurus corpus gets a fresh re-ingest (it is a crawl artifact,
not a user store — re-embedding is the accepted cost, ~30 min of embeds
at 1 req/s scale).

## Testing

TDD, existing seams (`HtmlCrawler.ParsePage` / `NormalizeLink` have direct
tests; `CrawlAsync` has integration tests against a local test server or
inline HTML — follow whichever pattern `CrawlerTests` already uses):

1. `ParsePage` lang cases: `lang="en"` → Lang "en"; `lang="en-US"` → kept;
   `lang="zh-CN"` → kept (field reports; the *skip* is the crawl loop's
   decision); absent → null.
2. `ParsePage` canonical cases: canonical link present → resolved absolute
   same-host Uri; off-host canonical → null (do NOT trust cross-site
   canonicals); absent → null.
3. `NormalizeIdentity`: `/docs/foo` ≡ `/docs/foo/` ≡ `/docs/foo/index.html`;
   `/docs/foo` ≠ `/docs/bar`.
4. `CrawlAsync` skip paths (integration): non-English page lands in `Skipped`
   with `non-English page (lang=zh-CN)`; duplicate canonical lands in
   `Skipped` with `duplicate of …`; neither appears in `Pages`.
5. Full suite green (151 + new).

## Success criteria

1. Suite green, all new tests pass.
2. **Live measurement on the flagship docusaurus store (the honest gate):**
   fresh crawl of docusaurus.io — zero non-English pages in the corpus
   (count in crawl summary), page count drops vs the 200-page pre-hygiene
   crawl (localized pages + duplicates gone), and the investigation's repro
   questions re-asked: the zh-CN-showcase retrieval path no longer occurs
   (citations never include a localized page).
3. Residual noise (blog posts on nav questions) recorded as the
   hybrid/rerank decision input — TODOS entry updated with the measurement.

## Not in scope

- Hybrid BM25/reranking — deferred behind the Success-criteria-2 measurement.
- Draft over-declining (investigation shape (b)) — revisit only after
  retrieval is clean, per the TODOS entry.
- `<meta http-equiv="content-language">`, HTTP `Content-Language` header,
  content-based language detection — `<html lang>` only (the docusaurus
  case, the one actually observed).
- Sitemap seeding, redirect-chain canonicalization, case-folding URLs.
- Strip of nav/SVG chrome at the chunk level (HTML-soup chunks from
  ReverseMarkdown) — a separate, later item if it still hurts after this.
- Env override for non-English crawls (v1 cut; README limitation line).

## What already exists (reused, not rebuilt)

- `HtmlCrawler.ParsePage` — AngleSharp DOM already parsed per page; two
  attribute reads added.
- `Skipped` list + ingest summary printing — skip reasons surface for free.
- `NormalizeLink` — same-host path-only URL rule; identity normalization
  layers on top.
- robots/noindex/content-type skip pattern in `CrawlAsync` — new skips copy
  the same shape.

## Failure modes

- **Over-collapse (`/foo` vs `/foo/` genuinely distinct):** only on sites
  serving distinct content under slash twins — none in the target class;
  the skip is recorded, not silent, so an affected crawl is visible in the
  summary.
- **Trusting canonical tags:** canonicals are honored only same-host
  (off-host canonical → null → finalUrl identity). A site with wrong
  self-canonicals could over-dedupe; recorded in `Skipped`, visible.
- **`lang` absent on localized pages:** unfiltered garbage persists for
  sites that don't tag; documented residual (docusaurus tags correctly —
  the observed case).
- **Fresh re-ingest cost:** ~30 min of crawl+embed for the flagship store,
  one time.

## Implementation Tasks

1. TDD: `ParsePage` lang + canonical field tests (RED) → fields + reads
   (GREEN).
2. TDD: `NormalizeIdentity` tests (RED) → helper (GREEN).
3. TDD: `CrawlAsync` integration tests for both skip paths (RED) → skip
   logic + frontier dedupe (GREEN).
4. README URL-ingest section + limitation line (English-only crawl).
5. TODOS: update the OPEN entry — shape (a) hygiene shipped, record the
   Success-criteria-2 measurement, keep hybrid decision deferred.
6. Live: fresh docusaurus crawl → measurement → repro questions re-ask.
7. Version bump v0.6.0 (crawl behavior change), commit, tag, watch
   workflows (user gate for release, per no-auto-commit convention).