# cs-agent

Verifier-gated support resolution engine — open-source Fin counterpart, showcase for
Microsoft Agent Framework (MAF) .NET handoff builder. C#/.NET, BYO model keys,
weekend-budget v1. Greenfield until Next Step 1.

## Read these first

1. `docs/designs/design-2026-09-05.md` — the plan of record (eng + CEO review CLEARED,
   0 unresolved decisions). Architecture, verifier rule, store, env-var contract,
   Test Plan, Next Steps 1-11. Self-contained.
2. `docs/designs/ceo-plan-2026-09-05.md` — scope decisions D1-D28 + landscape correction
   (README claim wording lives here — dated/scoped, re-verify at publish).
3. `docs/designs/test-plan-2026-09-05.md` — test contract. xUnit.
4. `docs/designs/TODOS.md` — deferred items (CI eval gate, post-publish).

## Invariants (do not violate without the user's explicit decision)

- **Verifier = ONE model call.** One prompt file, two labeled sections
  (claim-extraction, support-judgment), structured claims JSON out.
  Worst case 3 LLM calls per ask. The cost line and MCP sizing depend on this.
- **Verdict before output.** Draft is buffered; nothing prints (CLI/MCP/DevUI)
  until the resolve/escalate verdict exists. No streaming in v1.
- **Escalate is the safe default.** Any-unsupported, zero claims, or malformed
  verifier JSON twice → escalate. Never resolve vacuously, never print a rejected draft.
- **One result object, three surfaces.** answer · citations · claims[] · calls ·
  seconds · estimated_cost live in one object; CLI renders it (plus `--json`),
  MCP exposes fields, DevUI displays. No surface builds its own copy.
- **Validate loudly.** Bad env var, malformed fixture record, corrupt `.db`,
  unreadable file: named structured error, never a silent wrong answer.
- **eval exit code is the gate.** 0 on pass / 1 on miss vs the pass targets —
  holds on the shipped repo too (honest-fail).

## Build order (design doc "Next Steps" is authoritative)

Scaffold → go/no-go prototype (4-item checklist, any failure = STOP and re-decide
with the user, not a silent fallback) → store → ingest → real agents → eval →
tune → MCP → packaging → README. Eval is built BEFORE tuning (steps 6/7 were
swapped on purpose). On a tuning stall: swap the verify model BEFORE trimming
the fixture.

## Known limitations (documented debt — all intentional v1 cuts, not bugs)

1. **Static price table staleness** — cost line is an estimate vs a static table;
   prints "— (non-default model)" when draft/verify models are overridden.
   README carries the staleness caveat.
2. **Plain top-k search** — no reranking/hybrid. Documented limitation in README.
3. **Hallucination proxy undercounts** — counts zero-overlap resolutions only;
   reproducible by design, independent of the live verifier. Upgrade trigger:
   post-publish, alongside reranking.
4. **Tuning-set numbers in-sample** — the 25-question table is tuning-set fit;
   the held-out set (20 questions, `eval --heldout`) carries the
   generalization claim and is one-shot (missed questions replaced, not
   re-tuned). README carries both.

Plus the deferred queue: `docs/designs/TODOS.md` (CI eval gate) and the design
doc's post-publish order (URL crawl, HTTP API, Docker, Postgres, 10x
escalation-with-actions).

## Conventions

- Errors: structured error family — component + named var/record/file, non-zero
  exit, no stack traces.
- Tests: xUnit; model-dependent tests use the fake-or-real model seam.
- Env vars only, no config files. `CS_AGENT_MODEL_KEY` is the only required var.
- No auto-commits; commit only when the user asks.