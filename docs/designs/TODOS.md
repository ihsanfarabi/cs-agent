# TODOS — cs-agent

## TODO: CI eval gate on the stub model (P2, deferred 2026-09-05 by /plan-ceo-review D8, confirmed D26)

**What:** GitHub Actions job that runs `cs-agent eval` against the repo fixture on the
Test Plan's fake-model seam and fails the build when the pass targets are missed —
eval already exits 1 on miss, so the gate is just CI invoking eval and asserting exit 0.

**Why:** makes verifier-tuning regressions visible on every PR instead of discovered by
users; the eval exit-code semantics (accepted in the CEO review, D13/D16) exist precisely
so a machine can consume the pass targets.

**Pros:** every PR checked against the thesis's own numbers; a prompt tweak that breaks
the gate fails loudly and immediately.

**Cons:** fixture numbers on a stub model are not live-model truth (documented as such);
CI minutes cost; thresholds need maintenance as targets evolve.

**Context:** deferred because the weekend budget is provisional and CI work belongs
post-publish, beside the build/test/publish workflow. Pick-up notes: read the Test Plan's
fake-or-real model seam (`ihsanfarabi-unknown-eng-review-test-plan-20260905.md`), add a
workflow invoking eval on the stub, assert exit 0. Targets and their scaled cut-line
versions live in the design doc's eval fixture section.

**Effort:** M human team → S with CC+gstack.
**Depends on:** repo published + CI baseline (build/test workflow) existing.