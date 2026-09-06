# TODOS — cs-agent

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