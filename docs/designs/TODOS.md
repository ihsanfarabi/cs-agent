# TODOS — cs-agent

## OPEN: versioned-docs URLs flood top-k with near-clone chunks (issue #6, filed 2026-09-06)

Live crawl of docusaurus.io/docs: same page served at many versioned URLs
(`/docs/`, `/docs/3.9.2/`, `/docs/next/`) ingests as distinct pages; top-k=8
fills with near-identical chunks (scores 0.7457–0.7436), retrieval diversity
collapses, install question escalated 3/3 while non-duplicated questions
resolved first try. Fix candidates in the issue (URL version-segment dedupe /
chunk near-dup suppression / reranking). Natural pairing: the post-publish
reranking upgrade.

## OPEN: evidence flag on POST /ask — `?evidence=true` trace bundle (deferred 2026-09-07, HTTP-API eng review D9)

**What:** optional `?evidence=true` trace bundle: retrieved chunks, raw
verifier claims JSON, per-claim support — rejected-draft content clearly
labeled gated. (Contract re-shaped after the 202 upgrade: POST /ask returns
202 immediately, so the bundle rides GET /result/{id}, e.g.
`/result/{id}?evidence=true` on the done poll.)

**Why:** strongest portfolio demo of the verifier gate ("escalations
auditable over curl"); today an escalation's reasoning is only visible
via the CLI render.

**Context:** declined as Approach C in the 2026-09-07 office-hours session
(design-2026-09-07-http-api.md "Approaches") purely on plan-faithful
grounds, not merit. Cheap increment once the HTTP API ships. Start:
optional query param + response extension in CsAgent.Http, reuse
AskResult internals.

**Depends on:** HTTP API v1 shipped (plan of record:
design-2026-09-07-http-api.md).

## DONE: 202 + GET /result/{id} upgrade — triggered by measured p95, shipped with the HTTP API (2026-09-07; was eng review D10)

**Trigger:** p95 computed from existing `eval-results/*.json` (45 live
asks, 2026-09-06, default deepseek pair): combined **31.3s** (tuning 33.9s,
heldout 23.7s), 18/45 asks over the ~15s threshold — 2x over, so the
pre-authorized upgrade executed BEFORE any sync handler was built.

**What shipped (with the HTTP API v1):**
- POST /ask always returns 202 `{"id","status":"running","result"}` — sync
  /ask never existed (D17); GET /result/{id} polls status-in-body (D19):
  404 unknown · 200 running · 200 canonical AskResult when done · recorded
  registry 5xx replayed when errored.
- In-process ConcurrentDictionary job store, no eviction/persistence (D18):
  restart loses ids, resubmit to recover — documented in README limitation #5.
- CancellationToken threaded through AskPipeline.Run (the one deliberate
  Core change): retrieve/draft/verify observe it; shutdown cancels running
  jobs; VerifyOnce keeps fail-closed for every non-cancellation failure
  (cancellation propagates instead of fabricating a verdict).
- Eng-review D4 live pin: dead-BaseUrl check exposed the OpenAI/ClientModel
  retry path surfacing a BARE ClientResultException with no inner exception
  — the 502 is-or-wraps rule shipped with ClientResultException added, plus
  its own scripted test.
- Tests: 110/110 green (12 HTTP endpoint + 7 serve fail-fast + 2
  cancellation added); full suite in design-2026-09-07-http-api.md
  Implementation Tasks T0-T5 (all checked).

## DONE: URL crawl ingest mode (closed 2026-09-06; was design-doc post-publish item, issue #4)

**What shipped (PR #5, merged 7a0c18b):** `cs-agent ingest <url>` — same-domain
HTML crawl into a host-derived corpus (`docs.foo.com` →
`cs-agent-docs-foo-com.db`). AngleSharp DOM-parse (no JS execution) +
ReverseMarkdown conversion; ≤200 pages, depth ≤3, ≥1 req/s (+ robots
Crawl-delay when larger); robots.txt prefix-Disallow only, absent = allow,
5xx = fail closed with zero fetches; `<meta robots noindex>` skip; fragment+
query stripped; off-host links dropped. Prompts hardened for crawled corpora
(chunk delimiters + explicit data-not-instructions line; residual boundary
documented in README). Resume is pipeline-level (D8): crawl re-walks links,
content-hash skips re-embedding of unchanged pages — the spec's original
pre-fetch skip broke its own resume AC and was replaced after live smoke.

**Live eval (post-hardening prompts, run 34041476296, gate green):** tuning
17/20 · 5/5 · proxy 0 · Jaccard 0.93; held-out 15/16 · 4/4 · proxy 0 ·
Jaccard 0.91. Both above gate; README tables updated to the honest ranges.
Workflow lesson: tuning + held-out now run as PARALLEL jobs — a single serial
45-min job starved the held-out set on slow provider days (two cancelled runs).

**Known limits (documented in README):** JS-rendered sites, sitemaps,
auth-gated pages, and URL-based eval fixtures are out of scope v1 (verified
live against anglesharp.github.io, a JS SPA — crawler ingests 1 page, no
crash).

## DONE: Held-out eval set (closed 2026-09-06; was README/design-doc "first upgrade")

**What shipped:** `fixtures/questions-heldout.jsonl` — 20 fresh questions
(16 answerable across 14 pages, 4 unanswerable) authored against the same
20-page corpus after the verifier prompts were locked. `cs-agent eval
--heldout` scores it through the identical pipeline and gate (exit 0/1,
targets scale: ≥80% of 16 answerable, 4/4 unanswerable, proxy 0). CI stub
gate covers it (`EvalGateTests.ScriptedHeldOut_MeetsAllPassTargets` —
harness regressions only, same honest scope as the tuning gate). The
`live-eval` workflow runs both sets and posts both summaries.

**Live numbers (default all-deepseek pair, 2026-09-06 live-eval run):**
held-out 16/16 answerable, 4/4 unanswerable, proxy 0, citation Jaccard
0.97 — first-attempt pass, no prompt changes, so the set stays one-shot.
A local run scored 15/16 (one sampling-variance miss, different question).

**One-shot rule:** a held-out miss means fix prompt/model, then REPLACE the
missed question with a fresh one. Never re-tune against the miss and re-score
the same question as held-out. The rule is stated next to the README numbers.

## DONE: CI eval gate on the stub model (P2, closed 2026-09-06; was deferred 2026-09-05 by /plan-ceo-review D8, confirmed D26)

**What shipped:** `tests/CsAgent.Tests/EvalGateTests.cs` — scripted full-fixture
eval (25 records) on the fake-or-real model seam, run through `EvalRunner` with
the same wiring as `AskPipelineTests`. It rides the normal xUnit suite, so CI's
`dotnet test` step IS the gate (eval exit-code semantics preserved: a miss is a
red build). Ships with a negative control (one flipped script entry fails
`EvalScoring.Passes`) proving the gate is not vacuous. Workflows:
`.github/workflows/ci.yml` (build + test on push/PR to main).

**Honest scope (important):** the stub responders ignore prompt text, so this
gate catches HARNESS regressions only — scoring, fixture loading, resume,
pipeline wiring. It CANNOT catch prompt-tuning regressions; the earlier
phrasing "a prompt tweak that breaks the gate fails loudly" holds only for the
LIVE eval, which runs as a manual `workflow_dispatch` job
(`.github/workflows/live-eval.yml`, ~75 real model calls per run) and never
blocks push/PR/tag publishes.

**Original motivation (kept for the record):** make harness regressions visible on every
PR instead of discovered by users. The eval exit-code semantics (accepted in the CEO
review, D13/D16) exist precisely so a machine can consume the pass targets. Stub numbers
are not live-model truth — documented above and in the README's eval disclosure.