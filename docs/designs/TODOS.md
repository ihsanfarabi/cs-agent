# TODOS — cs-agent

## OPEN: image-hardening pass for the GHCR image (opened 2026-09-07 by /plan-eng-review on the Docker plan)

**What:** follow-up hardening of `ghcr.io/ihsanfarabi/cs-agent`: cosign signing
(+ provenance attestations), chiseled/distroless base instead of standard
aspnet:10.0, and a HEALTHCHECK-equivalent (the aspnet base ships no
curl/wget, so v1 ships no HEALTHCHECK directive; orchestrators probe
`GET /health`).

**Why:** v1 image is an unsigned standard-base showcase artifact — fine for
the demo story, not for real deployment. Hardening earns its keep when
someone deploys the image for real.

**Trigger:** first report of real-world deployment, or the post-publish
hardening sweep alongside the reranking upgrade. **Trigger note (2026-09-08,
eng review of the MMR plan):** the reranking upgrade is NOW in flight
(design-2026-09-08-mmr-diversity.md), so this sweep moment arrived — but the
entry's other line governs: "hardening earns its keep when someone deploys
the image for real", and no deployment exists yet. Queued as the next
increment candidate after MMR ships; not bundled into it.

**Where to start:** `.github/workflows/docker.yml` (add cosign step after
the push), `Dockerfile` (base swap; ENTRYPOINT unchanged), README
limitiation text. Design doc `docs/designs/design-2026-09-07-docker-ghcr.md`
"Not in scope" section lists the same items (that list goes stale; this
entry is the living one).

## DONE: MMR retrieval diversity — issue #6 closed (2026-09-08)

**What shipped:** `SqliteVectorStore.TopK` replaced by `Pool(query)` (every
chunk scored, relevance-ordered, vectors retained — same single scan, zero
extra DB work) + `Retriever.MmrSelect` (classic MMR, λ = 0.7 fixed in code,
first pick = highest relevance, set returned relevance-descending so
citation numbering is unchanged). Pool = whole scored set (no fetch_k — eng
review D1); MMR decides set, relevance decides order (D2). Result object,
cost model (1 embed, ≤3 LLM calls), env vars, schema: all unchanged; crawled
corpora benefit with no re-ingest. Verified on the real repro store (D5):
measured clone-twin vs adjacent-chunk similarity gap, then integration check
(clone vector as query → top-8 spans distinct pages, zero model calls).
Eval gates green on the default pair. README limitation #2 rewritten.
Deferred with it: ingest-time URL canonicalization; reranking/hybrid search
stay README limitations.

## DONE: Dockerfile + GHCR image (closed 2026-09-07; was design-doc post-publish item 3)

**What shipped:** multi-stage Dockerfile (framework-dependent CLI publish onto
aspnet:10.0, non-root `app` user, `/data` volume, `CS_AGENT_BIND=0.0.0.0` +
`CS_AGENT_STORE=/data/cs-agent.db` baked in) + `.github/workflows/docker.yml`
(PR + main push: amd64 build-only, never pushes, read-only token; tag v*: one
buildx build cross-compiled from the native SDK stage — no QEMU — pushing
`ghcr.io/ihsanfarabi/cs-agent` as a single multi-arch manifest with `vX.Y.Z`,
`X.Y.Z`, `latest`; own tag==csproj-version guard, `packages: write` scoped to
tag refs only, 15-min timeout). Core change: one — `ServeRunner` gained
`ParseBind` (`CS_AGENT_BIND`, default loopback, hostnames rejected, wildcard
allowed, IPv6 bracketed for UseUrls) plus the non-loopback stderr warning; bad
value is named error `serve/bad-bind`, exit 1, before any socket or store open.
Zero Core pipeline changes; eval gates untouched. Tests: 8 ParseBind cases +
3 Run tests added (fail-fast ordering + both warning directions, port-pinned
env hygiene; 130 total, all green). The image is the full CLI —
ingest and serve share the `/data` volume; MCP stays a NuGet tool (not in the
image, documented). Workflow note: the originally sketched native-runner
matrix was replaced by QEMU at plan time (per-platform matrix pushes overwrite
the tag manifest instead of merging), then QEMU was replaced by the
cross-compile pattern per outside-voice review (D9) — SDK runs native on
`$BUILDPLATFORM`, `TARGETARCH` picks the RID; design doc premise 6 records
both revisions.

## DONE: evidence flag on the done poll — GET /result/{id}?evidence=true (closed 2026-09-07; was eng review D9 / design Approach C)

**What shipped:** optional `?evidence=true` on the done poll returns
`{ result, evidence }` — the canonical AskResult byte-identical to the bare
poll, plus the trace: `draft_answer` (as written, even when the verifier
rejected it — it surfaces ONLY here, explicitly `draft_rejected: true`),
`draft_rejected`, and `verifier_raw` (the verifier's raw JSON; null when its
model call never reached the provider — never reconstructed). Core gained
`AskEvidence`/`AskRun` + `AskPipeline.RunWithEvidence` (one deliberate Core
change; `Run` delegates to it, so CLI/MCP/eval contracts are untouched and
no model call was added). Running/errored/unknown polls ignore the flag —
verdict-before-output holds: no trace exists before Done. Also shipped (the
one deliberate HTTP change beyond the flag): `Task.Yield` in
`AskJobStore.RunAsync` detaches job execution from the submit call stack —
under the fake test seam every await completes synchronously, so without it
the whole job ran inline inside POST /ask (the running window was
unobservable in tests; production has real async model calls). Tests: 9
added (4 pipeline-level, 5 endpoint-level incl. byte-identical nested
result); the bare-poll contract test passes unmodified; 119/119 green.

**Demo line:** escalations are now auditable over curl — poll with the
flag, read the rejected draft next to the verifier JSON that killed it.

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