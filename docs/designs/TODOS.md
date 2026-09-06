# TODOS — cs-agent

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
are not live-model truth — documented above and in the README's in-sample disclosure.