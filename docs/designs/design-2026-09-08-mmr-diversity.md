# Design: MMR retrieval diversity (issue #6)

Date: 2026-09-08. Closes issue #6
(fix: versioned docs URLs flood top-k with near-identical chunks).

## Problem

Live crawl of a versioned-docs site (`docusaurus.io/docs`, 200 pages, ~3,960
chunks) ingests the same page under many versioned URLs
(`/docs/installation`, `/docs/3.9.2/installation`, `/docs/3.5.2/installation`,
…). Plain top-k retrieval then returns near-clones for the whole result set
(all eight top chunks the same content, cosine 0.7457 down to 0.7436).
Retrieval diversity collapses, the draft never sees complementary pages, the
verifier correctly rejects the draft's claims, and the question escalates
(observed 3/3 runs on the install question while a question retrieving
non-duplicated pages resolved first try).

This is a **diversity** defect, not a relevance defect: clones score
identically, so any relevance reranker — the README limitation #2 upgrade —
would rank them adjacently too. Reranking stays deferred; this increment adds
diversity selection only.

## Approach

Classic MMR (maximal marginal relevance) over the existing chunk embeddings:

1. Embed the query (unchanged — still exactly one embedding call).
2. Store returns **every scored chunk** as the candidate pool
   (relevance-ordered, vectors retained — the store already loads and scores
   all chunk vectors in one scan, so the pool costs no extra DB work).
3. Retriever selects `topK` iteratively via MMR: each step picks the candidate
   maximizing

   `λ · cos(query, c) − (1−λ) · max_{s ∈ selected} cos(c, s)`

   with λ = 0.7; the first pick is the highest-relevance chunk. A clone has
   high relevance but near-1.0 similarity to its already-selected twin, so its
   margin goes negative and any decent chunk from a distinct page wins instead.

Constants are fixed in code, not env vars (agreed 2026-09-08): λ = 0.7. No
new `CS_AGENT_*` vars. Pool size is deliberately NOT a constant: the whole
scored set is the pool (eng review D1, 2026-09-08) — undersized fetch_k is
MMR's most-reported pitfall, and since the store scores every chunk in one
scan anyway, a full-set pool is immune to the undersized-fetch_k failure
mode at any corpus size. Honest cost (eng review D7): the store keeps the
redundant vectors and every query full-scans them — query-time healing is
the deliberate v1 cost; ingest-time canonicalization remains the deferred
source fix.

Why MMR over the alternatives considered:
- **Shingle-hash near-dup suppression** — text-processing code MMR doesn't
  need; Jaccard on text is weaker than embedding cosine for paraphrased
  versions (3.5.2 vs 3.9.2 content differs slightly); a second threshold to
  tune.
- **Ingest-time URL canonicalization** — site-pattern heuristic, requires
  re-ingest of existing corpora, no help for duplicate content in non-crawled
  corpora. (A possible later hardening; not this increment.)

Properties: pure in-process vector math — the worst-case-3-LLM-calls cost
invariant is untouched, no new dependencies, no schema change, and existing
crawled corpora benefit without re-ingest.

## Design detail

### `SqliteVectorStore`

`TopK(query, k)` is replaced by:

```csharp
public sealed record ChunkCandidate(ChunkHit Hit, float[] Vector);

/// <summary>Every chunk scored by cosine against the query, relevance-ordered, vectors retained.</summary>
public IReadOnlyList<ChunkCandidate> Pool(ReadOnlySpan<float> query)
```

Same full scan the store performs today (`TopK` already loads and scores
every chunk vector); the only change is returning the vectors alongside the
hits. `ChunkCandidate` lives in
`SqliteVectorStore.cs` beside `ChunkHit`. Only caller: `Retriever` (plus
tests).

### `Retriever`

```csharp
public const float Lambda = 0.7f;

/// <summary>
/// MMR selection: relevance to the query minus (1−λ) times the worst-case
/// similarity to anything already selected. Pure function over candidates.
/// Returns the selected set, relevance-descending.
/// </summary>
public static IReadOnlyList<ChunkCandidate> MmrSelect(
    ReadOnlySpan<float> query, IReadOnlyList<ChunkCandidate> pool, int k, float lambda)
```

Empty-set convention (eng review D7): the first pick has no selected set,
so the penalty term is 0 — the first pick is simply the highest-relevance
candidate. An empty pool returns an empty list (defensive; unreachable via
`Retrieve`, which throws `empty-store` first).

`Retrieve` becomes: embed → `store.Pool(vector)` → `MmrSelect` → sort the
selected set relevance-descending → map to `CitedChunk` numbered [1..k].

- **Ordering (eng review D2, 2026-09-08):** MMR decides the SET; relevance
  order decides the numbering. The citations[] scores stay descending —
  today's observable contract (CLI, `--json`, HTTP, MCP) is unchanged; only
  the membership of the set changes.
- `CitedChunk.Score` keeps its current meaning — query-relevance cosine, not
  the MMR margin — so citation scores stay comparable and the `--json` /
  HTTP / MCP result shape is unchanged.
- Small corpora: pool smaller than topK → return what exists (matches today's
  behavior when the store holds fewer chunks than k).
- No new error paths: `empty-store`, `dimension-mismatch`, and embed failures
  are unchanged.

### Edge case: adjacent same-page chunks

Chunks from one long page share a 150-char overlap and therefore high mutual
cosine. λ = 0.7 keeps relevance dominant: a genuinely needed second chunk
from a page stays selectable, while near-identical clones (sim ≈ 1.0) are
pushed out. The eval fixtures contain multi-chunk pages, so the eval gates
are the regression bar for this risk.

## Testing

- **Unit (synthetic vectors, no model calls):**
  - No redundancy → MMR set equals plain top-k set, relevance order preserved.
  - Clone set + distinct pages → clones collapse to one, distinct pages fill k.
  - λ weighting sanity: a diverse-but-weak candidate does not beat a strong
    relevant one.
  - Pool smaller than k → all candidates returned, no crash.
  - `Pool` returns relevance-ordered candidates with vectors attached.
  - Selected set is numbered relevance-descending even when MMR picked in a
    different order.
- **Store migration (regression, mandatory):** `Pool` inherits the
  `empty-store` guard from `TopK` (`SqliteVectorStore.cs:99` — dropping it
  would turn the named `store/empty-store` error into a silent empty
  result). The 4 direct `TopK` test call sites migrate to `Pool`
  (`SqliteVectorStoreTests.cs:29,47,55`, `CorpusLoaderTests.cs:155`),
  including `EmptyStore_TopK_ThrowsNamedError` → `EmptyStore_Pool_ThrowsNamedError`.
  All 130 existing tests stay green — every pipeline test then exercises
  MMR implicitly.
- **Local repro-store verification (eng review D5, part of done):** the
  2026-09-06 repro store `cs-agent-docusaurus-io.db` (repo root, gitignored,
  ~3,960 chunks) enables zero-model-call checks against the REAL failure
  data. Two steps, in order:
  1. **Measure the gap λ must survive** — read the store read-only; compute
     inter-clone cosines (same page content under versioned URLs) and
     adjacent same-page chunk cosines. Gate: clone-twin sims must sit
     clearly above adjacent-chunk sims with λ=0.7 separating them
     (break-even: selection flips when
     `0.3·(sim_gap) > 0.7·(relevance_gap)`). If the gap is absent, STOP and
     re-decide λ on the numbers before implementing.
  2. **MMR integration check on real vectors** — take a stored clone
     chunk's own vector as the query (no embedding call), run
     `Pool` + `MmrSelect`, assert the selected top-k spans distinct pages
     instead of the versioned-URL clones.
- **Eval gates:** tuning set and held-out set must stay green, unchanged
  (MMR is a near-noop on a corpus without clones; any miss is a regression
  signal, most plausibly the adjacent-chunk risk). **Miss procedure
  (pre-committed, eng review D6):** if a gate goes red after MMR lands,
  revert the increment and reopen issue #6 with the D5 measurements
  attached — λ is re-decided against numbers, not tweaked in flight under
  a red gate (matches the build-order precedent: swap the model before
  trimming the fixture).
- **Live docusaurus re-run is a follow-up, not the done-definition** (agreed
  2026-09-08): the live corpus may have drifted since the 2026-09-06 repro,
  and the unit + D5-measurement bar carries the correctness claim. The
  re-run is recorded as post-merge verification on the freshly crawled
  corpus.

## Success criteria

- Clone-flooded store returns a top-k dominated by distinct pages — proven on
  synthetic vectors AND on the real docusaurus repro store (D5 integration
  check, zero model calls).
- Measured clone-twin vs adjacent-chunk similarity gap exists in the repro
  store, with λ=0.7 on the separating side (D5 measurement gate).
- Eval gates hold: tuning ≥ 80% answerable, 5/5 (heldout 4/4) unanswerable
  escalated, proxy 0, on the default pair.
- Result object, cost model, env-var contract, store schema: all unchanged.
- README limitation #2 rewritten: MMR diversity (clone suppression) present;
  relevance reranking and hybrid search still absent.

## Not in scope

- Relevance reranking (cross-encoder or LLM) and hybrid retrieval — remain
  README limitations; an LLM reranker would also break the 3-call cost
  invariant (would need the user's explicit decision).
- Ingest-time URL canonicalization / version-segment detection.
- Hallucination-proxy upgrade (separate deferred item).
- Image-hardening pass (cosign/chiseled/HEALTHCHECK) — TODOS entry's sweep
  trigger fired (this is the reranking upgrade) but the deployment trigger
  governs; queued as the next increment candidate (eng review D4).

## What already exists (reused, not rebuilt)

- `VectorMath.Cosine` — MMR and the store's scoring reuse it; no new
  similarity code.
- `TopK`'s existing full scan (`SqliteVectorStore.cs:104-124`) already loads
  and scores every chunk vector — the full-set candidate pool adds zero DB
  work.
- Fake-embedding test seam (`FakeEmbeddingGenerator`) — synthetic-vector MMR
  tests fit the existing pattern.
- The 2026-09-06 docusaurus repro store (`cs-agent-docusaurus-io.db`) — real
  clone vectors for the D5 measurement and integration checks, zero model
  calls.

## Failure modes

| Failure | Test/guard | User sees |
|---|---|---|
| λ=0.7 fails to separate real clone sims from adjacent-chunk sims | D5 measurement STOP-gate before implementation | Nothing ships — re-decided on numbers |
| MMR displaces a needed adjacent chunk | Eval gates (tuning + heldout); D6 pre-committed revert + reopen | Honest-fail eval exit 1, revert, issue #6 reopened with measurements |
| Empty-store guard lost in TopK→Pool migration | Migrated `EmptyStore_Pool_ThrowsNamedError` test | Named `store/empty-store` error preserved |
| Empty pool / empty-max in first MMR pick | Convention documented (penalty = 0); empty-pool unit test | No crash |
| Clone-only corpus | MMR reorders, never drops — pool is the whole scored set | k chunks still returned |

No critical gaps — every failure mode has a test, a guard, or a pre-committed
procedure.

## Worktree parallelization

Sequential implementation, no parallelization opportunity — 2 files in the
same module (`src/CsAgent.Core/`) plus tests; T1 (store) blocks T2
(retriever), T2 blocks T3/T4.

## Implementation Tasks

Synthesized from this review's findings. Run via
superpowers:writing-plans → subagent-driven-development; checkbox as you ship.

- [ ] **T0 (P1, human: ~30min / CC: ~5min)** — verification — measure repro-store clone vs adjacent-chunk cosines; STOP if the λ=0.7 separation gap is absent
  - Surfaced by: outside voice P2 / review D5.1 — the break-even was asserted, never computed; `cs-agent-docusaurus-io.db` verified present (31MB, repo root)
  - Files: `cs-agent-docusaurus-io.db` (read-only), spec Testing section
- [ ] **T1 (P1, human: ~1h / CC: ~10min)** — store — `TopK` → `Pool(query)` with vectors retained, empty-store guard inherited, 4 test call sites migrated
  - Surfaced by: outside voice P3 — guard lives inside `TopK` (`SqliteVectorStore.cs:99`); dropping it turns `store/empty-store` into a silent empty result
  - Files: `src/CsAgent.Core/SqliteVectorStore.cs`, `tests/CsAgent.Tests/SqliteVectorStoreTests.cs`, `tests/CsAgent.Tests/CorpusLoaderTests.cs`
- [ ] **T2 (P1, human: ~1h / CC: ~10min)** — retriever — `MmrSelect` pure function (λ=0.7, empty-max=0) + `Retrieve` wiring + relevance-descending numbering
  - Surfaced by: design core; ordering per review D2 (MMR decides set, relevance order decides numbering)
  - Files: `src/CsAgent.Core/Retriever.cs`
- [ ] **T3 (P1, human: ~1h / CC: ~15min)** — tests — no-redundancy set equality, clone collapse, λ dominance, pool<k, empty pool, Pool ordering, descending numbering
  - Surfaced by: test review coverage diagram — 2 gaps (empty-pool unit, ordering)
  - Files: `tests/CsAgent.Tests/`
- [ ] **T4 (P1, human: ~45min / CC: ~10min)** — verification — D5.2 integration check on real repro vectors: clone chunk's own vector as query, assert distinct-page top-k
  - Surfaced by: outside voice P1 — synthetic vectors prove the mechanism, not the defect
  - Files: `cs-agent-docusaurus-io.db` (read-only), `src/CsAgent.Core/Retriever.cs`
- [ ] **T5 (P2, human: ~30min / CC: ~10min)** — docs — README limitation #2 rewrite, issue #6 close note, eval gates run
  - Surfaced by: design success criteria; D6 miss procedure pre-committed
  - Files: `README.md`

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 0 | — | not run — internal retrieval fix, no product-direction change |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | — | not run — no Codex subscription; outside voice ran as Claude subagent |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | CLEAR (PLAN) | 11 issues, 0 critical gaps — all folded into the spec (D1–D7) |
| Design Review | `/plan-design-review` | UI/UX gaps | 0 | — | not run — no UI surface touched |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — | not run — no API/config surface change |

- **CROSS-MODEL:** outside voice (Claude subagent, fresh context — same model
  family, weigh accordingly) found 6 issues: 2 material (done-definition
  never touches real defect; break-even never computed — both accepted as
  the D5 measurement + integration checks), 1 procedural (miss procedure —
  accepted as D6), 3 doc fixes (accepted as D7). 0 rejected. No tension
  remained unresolved.
- **VERDICT:** ENG CLEARED — ready to implement via writing-plans.

NO UNRESOLVED DECISIONS