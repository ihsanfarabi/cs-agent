# TODOS — cs-agent

## OPEN: draft-decline escalations on crawled corpora (opened 2026-09-08, zero-claims investigation)

**What the investigation found (2026-09-08):** every observed "zero-claims
verifier escalation" on the docusaurus store was the DRAFT declining ("The
ingested docs do not answer this.") with the verifier correctly returning
`[]` — the verifier prompt is NOT the problem and needs no change. Two upstream
shapes, both live-reproduced: (a) garbage retrieval — top-8 for a navbar
question was blog release posts, a zh-CN showcase page, and ReverseMarkdown
HTML-soup chunks (`<div class="browserWindowMenuIcon_Vhuh">`, SVG path data);
(b) draft over-declining on good chunks — a versioning question cited
docusaurus.io/versions at 0.72 and the draft declined anyway.

**Where this goes:** shape (a) hygiene SHIPPED 2026-09-08
(design-2026-09-08-ingest-hygiene.md, plan 2026-09-08-ingest-hygiene.md):
the crawl now skips non-English pages (`<html lang>` filter, English-only v1
cut, no env override) and dedupes page identities (same-host `<link
rel=canonical>` + trailing-slash/`index.html` twins) — both recorded as
`skipped:` lines in the ingest summary. Live measurement on a fresh
docusaurus crawl recorded below. The remaining shape-(a) residual — legit
pages (blog release posts, nav chrome) polluting top-8 on out-of-domain
questions — is the input to the deferred hybrid/rerank decision, which stays
behind that measurement. Shape (b) is draft-model judgment on the default
pair — revisit only after retrieval is clean. No separate verifier work.

**Live measurement (fresh docusaurus crawl, 2026-09-08, post-hygiene):**
3,210 chunks from 200 pages vs 4,314 pre-hygiene (−26%); 92 localized pages
skipped at fetch (zh-CN, ko, fr, pt-BR), 0 canonical-duplicate skips
(docusaurus canonical tags are self-referential), slash twins collapsed
pre-fetch (silent by design). Both investigation repro shapes resolve on
the fresh store: the navbar-config question (was retrieving blog + zh-CN +
HTML-soup) cites `/docs/api/docusaurus-config` and `/docs/blog`; the
versioning question (shape (b), previously declined on a 0.72-relevance
cited page) resolves citing migration + release-process pages. Honest
caveat: two resolved repros do not isolate hygiene from model variance —
the zh-CN garbage path is dead by construction (those pages are never
ingested), but shape (b) may still recur; the hybrid/rerank decision input
is now measured against a clean corpus.

**Hybrid/rerank decision (closed 2026-09-08): DO-NOTHING.** Probe on the
clean post-hygiene store (`cs-agent-docusaurus-io.db`, 3,210 chunks), 10
asks + re-runs. In-domain: navbar/versioning/i18n resolved with correct-page
citations; deploy — retrieval perfect (`/docs/deployment` top-1 at 0.837;
raw top-8 inspected, zero model calls) and both escalates were verifier-route
stalls failing closed (the 240s ceiling fired, 577s wall); Mermaid — the
answer page (`/docs/markdown-features/diagrams`) was NEVER INGESTED: the
corpus is exactly 200 pages (crawl cap) and the markdown-features family
reached only `toc`, so the escalates are correct honest behavior — blog
release posts + migration page in top-8 is the best available content, and
the one resolved run cited release 2.2, a legit page that answers the
question. Out-of-domain ×5 (SLA, pricing, Stripe API, support phone,
roadmap): 5/5 escalate, zero false resolves. Verdict: no observed miss
anywhere the answer page was actually in the corpus → plain top-k + MMR
holds; README limitation #2 stays. Revisit trigger: first genuine live
in-domain miss on a clean corpus WITH the answer page present. Probe
side-findings: (1) crawl-cap truncation — docusaurus.io needs more than 200
pages, deeper docs are missing (documented limit; future probes must author
questions against ingested pages only); (2) versioned-docs duplicates
(`/docs/3.x.y/cli` ×7, ~0.799 each) flood the pool — same class as
the deferred URL canonicalization, harmless at current scale (current
`/docs/cli` also present, top-1 correct on the deploy probe).

**Side finding (fixed same day):** the same investigation reproduced an
unbounded provider stall — an OpenRouter route accepts the verify call
(json_schema response_format with MAF's embedded `$schema` keys), returns 200
+ headers, and never streams the body. No framework default fires (ClientModel
1.14's shared HttpClient disables HttpClient.Timeout; its per-message 100s
NetworkTimeout did not surface). Fix: per model-call ceiling in AskPipeline
(240s, race-based) — draft timeout = structured `model/call-timeout` error,
verify timeout = one retry then fail-closed escalate with a "timed out" note.
Gap closed same day (2026-09-08): the ceiling was extracted into the shared
`ModelCallTimeout` helper and now bounds BOTH embedding call sites — the
query embed in `Retriever` and the batch embed in `IngestPipeline` (stage
name "embedding", same `model/call-timeout` error; never observed stalled,
closed by inspection of the same stall class). Suite 160.

## DONE: image-hardening pass for the GHCR image (closed 2026-09-08, v0.5.0; was opened 2026-09-07 by /plan-eng-review on the Docker plan)

**What shipped:** (1) runtime base swapped `aspnet:10.0` →
`aspnet:10.0-noble-chiseled` (Ubuntu chiseled/distroless: no shell, no
package manager, non-root `app` UID 1654 by default). The old `RUN mkdir /
data && chown` was shell-dependent — chiseled has no shell, so `/data` is now
created in the build stage and `COPY --from=build --chown=1654:1654`-d in;
a `.keep` placeholder guards against builders skipping empty dirs. The
--chown preserves the named-volume ownership mechanism (first mount copies
app-owned /data into the volume) — verified live with a fresh volume.
(2) `cs-agent healthcheck [--port N]` — new keyless, model-free CLI
subcommand (GET `http://127.0.0.1:PORT/health`, same port precedence as
serve; Program.cs dispatches it BEFORE config construction so keyless
containers work) + the image's HEALTHCHECK directive runs it (exec form —
no shell). Every failure is a named structured error (`healthcheck/bad-args`,
`bad-port`, `unreachable`, `timeout`, `bad-status`), success silent.
Adjacent fixes shipped with it: `--version` also moved before config (it
required a model key before) and now reads the assembly version (the
hardcoded `0.1.0` string had gone stale). (3) docker.yml publish job:
`id-token: write`, buildx `provenance: true`, keyless `cosign sign` of the
pushed manifest digest (digest, not tag aliases — same bytes), anchore
sbom-action CycloneDX + `cosign attest --type cyclonedx`. Build job
untouched (still catches Dockerfile rot). Tests: HealthCheckRunnerTests
(10: Kestrel seam ok/env-port/refused/non-200/bad-args/bad-port-env/timeout,
zero model calls), suite 151/151 green. Smoke: local chiseled build, fresh
named-volume ingest, serve → HEALTHCHECK healthy, keyless healthcheck
exit 1 unreachable (not missing-required-env), real ask resolved inside
the container (invariant-globalization safe — no `-extra` ICU variant
needed), multi-arch amd64+arm64 build green.

**Honest caveats:** plain chiseled ships no ICU (invariant globalization) —
live smoke passed, fallback `-extra` documented here if a culture issue
surfaces. Buildx provenance is a native referrer, not a cosign-signed DSSE:
read it with `docker buildx imagetools inspect`, not `cosign
verify-attestation` (the SBOM attestation IS cosign-verifiable). Signing
runs after the push: a failed sign step leaves an unsigned image public on
an immutable tag; the red workflow is the signal and `cosign sign` on the
same digest is idempotent on re-run. A `CS_AGENT_BIND` override to a
specific non-loopback IP dodges the built-in probe (documented in README).

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