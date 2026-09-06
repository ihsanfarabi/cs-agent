# Held-Out Eval Set Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a held-out eval fixture (20 fresh hand-labeled questions) so published eval numbers measure generalization, not prompt-tuning memorization, and retire the in-sample disclosure.

**Architecture:** One new fixture file (`fixtures/questions-heldout.jsonl`) over the existing 20-page corpus — no new docs, no store changes. `EvalRunner`, `EvalScoring`, `EvalQuestionResult` are unchanged; the scoring targets are already parametric (≥80% of answerable, ALL unanswerable escalated, proxy 0), so 16 answerable + 4 unanswerable gates with zero core-code change. Three touch points: CLI gains `eval --heldout`, the CI stub gate runs the held-out fixture through the same scripted seam, and the manual `live-eval` workflow runs both sets. README numbers for the held-out set come from one live run of the default pair only (all-deepseek) — running every pair doubles model spend for little information.

**Tech Stack:** .NET 10, xUnit, GitHub Actions.

**Spec:** No dedicated spec doc. Requirements live in:
- `README.md` "Eval results (honest numbers)" — the in-sample caveat this work removes (line ~114: "A held-out set is the first upgrade once the fixture grows")
- `docs/designs/design-2026-09-05.md` "Eval fixture schema and scoring" appendix + "In-sample disclosure" paragraph — record schema, scoring math, pass targets
- `docs/designs/TODOS.md` — DONE-entry pattern for shipped deferred items

## Global Constraints

- **No auto-commits.** Project rule: commit only when the user asks. Every task below ends at a verified state and reports — no `git commit` steps. The user commits.
- **Prefix every shell command with `rtk`** (`rtk dotnet test`, `rtk git status`, …) — RTK proxy, token savings.
- **Eval exit code is the gate:** 0 on pass, 1 on a miss, for BOTH sets. Holds on the shipped repo (honest-fail). A held-out miss is recorded, never trimmed.
- **One-shot rule (the integrity rule that makes this a held-out set):** if the live held-out run misses a question, the fix is (a) fix the prompt/model, then (b) REPLACE that question with a fresh one in the same JSONL. A question whose miss was tuned against is in-sample from that moment — never re-score it as held-out. This rule ships in the README, next to the numbers.
- **Fixture record schema, verbatim from the design doc:** `{ question, expected_answer, expected_sources[], expected_outcome }` — `expected_outcome` is `"resolved"` or `"escalate"`; escalate records have empty `expected_sources`; resolved records name ≥1 page; `expected_sources` are PAGE-level relative paths (bare filenames — the tuning set uses `"api-keys.md"` style).
- **Pass targets on the held-out set:** ≥80% of 16 answerable resolved, 4/4 unanswerable escalated, hallucination proxy 0. `EvalScoring.Passes` already implements exactly this — do not fork scoring.
- **Validate loudly:** malformed fixture records fail with the record's line number (FixtureLoader already does this — no change).
- **Held-out questions must not duplicate tuning-set questions or their answers.** Authored fresh below; verified in Task 1's sanity asserts.
- Tests: xUnit, fake-or-real model seam. The stub gate catches harness regressions only — never rebrand it as prompt-tuning coverage (TODOS.md "Honest scope" paragraph).

---

### Task 1: Held-out fixture + scripted CI gate over it

**Files:**
- Create: `fixtures/questions-heldout.jsonl`
- Modify: `tests/CsAgent.Tests/EvalGateTests.cs`

**Interfaces:**
- Consumes: `FixtureLoader.FromJsonl(path)` (existing), `EvalRunner.Run(records, fresh)` (existing), `EvalScoring.Passes` (existing).
- Produces: `fixtures/questions-heldout.jsonl` — 20 records, 16 `resolved` + 4 `escalate`, consumed by Task 2 (CLI) and Task 3 (workflow). Test csproj already copies `fixtures/**` to output (`CsAgent.Tests.csproj` line 31 glob), so the file reaches tests with no csproj change.

- [ ] **Step 1: Write the failing tests** — in `EvalGateTests.cs`, parameterize the scripted runner and add the held-out gate test. Change the signature and call sites:

```csharp
// BEFORE:
// private (EvalMetrics Metrics, IReadOnlyList<EvalQuestionResult> Results, int Skipped) RunScripted(
//     IReadOnlyCollection<string> escalateFlips)
// ... records = FixtureLoader.FromJsonl(Path.Combine(FixtureRoot, "questions.jsonl"));
//     Assert.Equal(25, records.Count);

// AFTER — replace the signature and the two lines above:
private (EvalMetrics Metrics, IReadOnlyList<EvalQuestionResult> Results, int Skipped) RunScripted(
    IReadOnlyCollection<string> escalateFlips,
    string fixtureFile = "questions.jsonl")
{
    var records = FixtureLoader.FromJsonl(Path.Combine(FixtureRoot, fixtureFile));
    Assert.True(records.Count is 25 or 20,
        $"unexpected fixture size: {fixtureFile} has {records.Count} records");
    // ... rest of the method body is UNCHANGED — byQuestion/flips/ingest/fake
    // agents/runner all key off `records`, not the filename
```

Then append this new test after `ScriptedEval_MeetsAllPassTargets`:

```csharp
/// <summary>
/// Held-out fixture (20 records: 16 answerable, 4 unanswerable) through the
/// same scripted seam. Proves the file parses, the labels satisfy the loader's
/// invariants, and the pass targets scale (≥80% of 16, 4/4, proxy 0). The
/// one-shot rule lives in the README; this gate never scores prompt quality.
/// </summary>
[Fact]
public void ScriptedHeldOut_MeetsAllPassTargets()
{
    var (metrics, results, skipped) = RunScripted(escalateFlips: [], fixtureFile: "questions-heldout.jsonl");

    Assert.Equal(20, results.Count);
    Assert.Equal(0, skipped);
    Assert.Equal(16, metrics.AnswerableTotal);
    Assert.Equal(16, metrics.AnswerableResolved);
    Assert.Equal(4, metrics.UnanswerableTotal);
    Assert.Equal(4, metrics.UnanswerableEscalated);
    Assert.Equal(0, metrics.HallucinationProxy);
    Assert.True(EvalScoring.Passes(metrics),
        $"held-out gate must pass on the scripted fixture, got {metrics.AnswerableResolved}/{metrics.AnswerableTotal} answerable, " +
        $"{metrics.UnanswerableEscalated}/{metrics.UnanswerableTotal} unanswerable, proxy {metrics.HallucinationProxy}");
}
```

- [ ] **Step 2: Run the tests, verify they fail for the right reason**

Run: `rtk dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~EvalGateTests"`
Expected: `ScriptedHeldOut_MeetsAllPassTargets` FAILS with the structured `fixture-not-found` error (file doesn't exist yet); the two existing tests still PASS.

- [ ] **Step 3: Create `fixtures/questions-heldout.jsonl`** — the full file, one JSON object per line (16 answerable across 14 distinct pages, 4 unanswerable; no answer duplicates any tuning-set question):

```jsonl
{"question": "Can an API key created by a read-only member deploy?", "expected_answer": "No; keys inherit the role of the member who created them.", "expected_sources": ["api-keys.md"], "expected_outcome": "resolved"}
{"question": "Where do I create a personal access token?", "expected_answer": "Settings → Access tokens; the token carries the same scopes the user has.", "expected_sources": ["authentication.md"], "expected_outcome": "resolved"}
{"question": "How do third-party applications get access to my account?", "expected_answer": "Through OAuth; apps request scopes explicitly and you approve each scope during authorization.", "expected_sources": ["authentication.md"], "expected_outcome": "resolved"}
{"question": "When am I billed for usage beyond my plan limits?", "expected_answer": "Overages are invoiced at the end of the billing cycle, appearing as a separate line on the next invoice.", "expected_sources": ["billing.md"], "expected_outcome": "resolved"}
{"question": "How do I install the CLI?", "expected_answer": "npm install -g @nimbus/cli, or brew install nimbus.", "expected_sources": ["cli.md"], "expected_outcome": "resolved"}
{"question": "Is a GDPR data processing agreement available?", "expected_answer": "Yes, for all customers on request.", "expected_sources": ["compliance.md"], "expected_outcome": "resolved"}
{"question": "How long are backups kept?", "expected_answer": "Backups run nightly and are kept for 14 days, for disaster recovery only.", "expected_sources": ["data-retention.md"], "expected_outcome": "resolved"}
{"question": "Who can take point-in-time snapshots?", "expected_answer": "Enterprise workspaces, through the API.", "expected_sources": ["export.md"], "expected_outcome": "resolved"}
{"question": "How quickly are incident postmortems published?", "expected_answer": "Within 5 business days of incident resolution.", "expected_sources": ["incidents-status.md"], "expected_outcome": "resolved"}
{"question": "What does the Slack integration do?", "expected_answer": "Posts deploy and incident alerts to a channel you choose; configured from Settings → Integrations.", "expected_sources": ["integrations.md"], "expected_outcome": "resolved"}
{"question": "How do I import an existing project?", "expected_answer": "Run nimbus import; it connects to the source repository and copies the configuration.", "expected_sources": ["migrations.md"], "expected_outcome": "resolved"}
{"question": "What happens if a schema migration fails during a deploy?", "expected_answer": "Migrations run in order and stop the deploy when one fails.", "expected_sources": ["migrations.md"], "expected_outcome": "resolved"}
{"question": "How are notifications delivered?", "expected_answer": "By email and by webhook events; channels are chosen per notification type.", "expected_sources": ["notifications.md"], "expected_outcome": "resolved"}
{"question": "What commands do I run for my first deploy?", "expected_answer": "nimbus init, then nimbus deploy; the first deploy runs a free build.", "expected_sources": ["quickstart.md"], "expected_outcome": "resolved"}
{"question": "What does a 429 response tell me?", "expected_answer": "You exceeded your rate limit; the Retry-After header gives the seconds to wait, then back off and retry.", "expected_sources": ["rate-limits.md"], "expected_outcome": "resolved"}
{"question": "What does nimbus sandbox reset do?", "expected_answer": "Deletes all sandbox data and restores the default template.", "expected_sources": ["sandbox.md"], "expected_outcome": "resolved"}
{"question": "How do I enable dark mode in the dashboard?", "expected_answer": "", "expected_sources": [], "expected_outcome": "escalate"}
{"question": "What is the maximum payload size for a webhook event?", "expected_answer": "", "expected_sources": [], "expected_outcome": "escalate"}
{"question": "Can I self-host Nimbus?", "expected_answer": "", "expected_sources": [], "expected_outcome": "escalate"}
{"question": "How do I change the email address on my account?", "expected_answer": "", "expected_sources": [], "expected_outcome": "escalate"}
```

Label provenance (each answerable record restates one fact from its single source page — the fixture ingests from `fixtures/docs`): key permissions / api-keys.md; token creation + OAuth apps / authentication.md; overages / billing.md; npm+brew install / cli.md; GDPR DPA / compliance.md; backups / data-retention.md; Enterprise snapshots / export.md; postmortems / incidents-status.md; Slack channel alerts / integrations.md; `nimbus import` + migration failure / migrations.md; email+webhook channels / notifications.md; `nimbus init`/`deploy` + free build / quickstart.md; 429 + Retry-After / rate-limits.md; sandbox reset / sandbox.md. The four escalates are deliberately adjacent to real pages (webhook payload size sits next to webhooks.md) — topical adjacency must not count as support.

- [ ] **Step 4: Run the tests, verify all three pass**

Run: `rtk dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~EvalGateTests"`
Expected: 3/3 PASS, including `ScriptedHeldOut_MeetsAllPassTargets`.

- [ ] **Step 5: Run the full suite** — no other test regressed.

Run: `rtk dotnet test -c Release`
Expected: all PASS. Report to user; user commits.

---

### Task 2: CLI `eval --heldout`

**Files:**
- Modify: `src/CsAgent.Cli/Program.cs:59-97` (the `Eval` local function) and the usage line at `src/CsAgent.Cli/Program.cs:142`

**Interfaces:**
- Consumes: `fixtures/questions-heldout.jsonl` from Task 1; `FixtureLoader`, `EvalRunner`, `EvalScoring` unchanged.
- Produces: `cs-agent eval --heldout [--fresh]` — loads the held-out fixture, persists results to `./eval-results/heldout/` (already inside the `eval-results/` gitignore rule — no .gitignore change), exits 0/1 by the same gate. Task 3's workflow invokes it.

- [ ] **Step 1: Write the CLI change.** The CLI has no unit-test seam for its local functions (existing pattern: gate tests cover the pipeline, CLI is verified by running). Replace the head of the `Eval` function:

```csharp
int Eval(string[] evalArgs, CsAgentConfig cfg)
{
    var fresh = evalArgs.Contains("--fresh");
    var heldOut = evalArgs.Contains("--heldout");
    var fixturePath = heldOut ? "fixtures/questions-heldout.jsonl" : "fixtures/questions.jsonl";
    if (!File.Exists(fixturePath))
        throw new CsAgentException(new CsAgentError(
            "eval", "fixture-not-found",
            $"{fixturePath} not found — run eval from the repo root"));
    var records = FixtureLoader.FromJsonl(fixturePath);
```

(Deletes the old ternary that threw inline — the `File.Exists` check above replaces it. The rest of the function is unchanged down to `var resultsDir = "./eval-results";`, which becomes:)

```csharp
    var resultsDir = heldOut ? "./eval-results/heldout" : "./eval-results";
```

And the PASS/FAIL message — it hardcodes "5/5 unanswerable", wrong for the held-out set. Replace lines 92-96:

```csharp
    var passed = EvalScoring.Passes(metrics);
    var set = heldOut ? "held-out" : "tuning";
    Console.WriteLine(passed
        ? $"PASS  {set} set: targets met (≥80% answerable, {metrics.UnanswerableEscalated}/{metrics.UnanswerableTotal} unanswerable, proxy 0)"
        : $"FAIL  {set} set: targets not met — exit 1 (honest-fail gate)");
    return passed ? 0 : 1;
```

Update the usage line at the bottom of `Program.cs`:

```csharp
Console.Error.WriteLine("usage: cs-agent <ingest|ask|eval [--heldout] [--fresh]|...> ...");
```

(Read the current line first and keep its exact remaining content — the edit only inserts `[--heldout] [--fresh]` after `eval`.)

- [ ] **Step 2: Build + suite**

Run: `rtk dotnet build -c Release && rtk dotnet test -c Release --no-build`
Expected: build clean, all tests PASS.

- [ ] **Step 3: Verify the flag end-to-end.** If `CS_AGENT_MODEL_KEY` is set in the environment, run:

```bash
rtk dotnet run --project src/CsAgent.Cli -c Release -- eval --heldout
```

Expected: `ingested fixtures: ... resumed` line, 20 questions scored, results land in `./eval-results/heldout/`, verdict line says "PASS held-out set" or "FAIL held-out set", exit code 0/1. Record the three numbers for Task 5 — this local run can stand in for the workflow numbers if it uses the default pair.
If no key is available, verify wiring with the stub seam only (Task 1 tests) and take the real numbers from Task 4's workflow run instead — do not fake them.

- [ ] **Step 4: Verify resume bookkeeping is separate.** Re-run the command from Step 3 without `--fresh`:

```bash
rtk dotnet run --project src/CsAgent.Cli -c Release -- eval --heldout
```

Expected: `eval: 20 questions (20 resumed)` — held-out results do not collide with the tuning set's files (different directory), and the tuning set's `eval-results/` files are untouched. Report to user; user commits.

---

### Task 3: `live-eval` workflow runs both sets

**Files:**
- Modify: `.github/workflows/live-eval.yml`

**Interfaces:**
- Consumes: `eval --heldout` from Task 2.
- Produces: a manual workflow_dispatch run that executes both sets, posts both outputs to the job summary, and fails if either set misses its targets. Task 4 triggers it.

- [ ] **Step 1: Edit the workflow.** Read the current file, then apply three edits.

Edit 1 — header comment (lines 3-4) and timeout (line 13), because the run grows from ~75 to ~125 model calls:

```yaml
# Manual only — never on push/PR, never blocks anything. Real model calls
# (~125 per run, all-deepseek defaults): tuning set (25) then held-out set (20).
```

```yaml
    timeout-minutes: 25
```

Edit 2 — replace the single "Run live eval" step (lines 23-27) with two independent steps plus a gate, so a tuning-set miss does not hide the held-out run:

```yaml
      # eval exit code is the gate: 0 pass / 1 miss (honest-fail). Each set
      # runs even if the other missed, so both truths reach the summary.
      - name: Run live eval (tuning set)
        id: tuning
        continue-on-error: true
        run: |
          set -o pipefail
          dotnet run --project src/CsAgent.Cli -c Release --no-build -- eval --fresh | tee eval.out

      - name: Run live eval (held-out set)
        id: heldout
        continue-on-error: true
        run: |
          set -o pipefail
          dotnet run --project src/CsAgent.Cli -c Release --no-build -- eval --heldout --fresh | tee eval-heldout.out

      - name: Gate — both sets must pass
        if: always()
        run: |
          echo "tuning:  ${{ steps.tuning.outcome }}"
          echo "heldout: ${{ steps.heldout.outcome }}"
          test "${{ steps.tuning.outcome }}" = success -a "${{ steps.heldout.outcome }}" = success
```

Edit 3 — extend the summary step (runs `if: always()`, so it already exists with that guard) to include the held-out output after the tuning block:

```yaml
            echo
            echo "## Held-out eval — $GITHUB_REF_NAME"
            echo
            echo '```'
            cat eval-heldout.out
            echo '```'
```

- [ ] **Step 2: Syntax-check the YAML locally**

Run: `rtk gh workflow list` (confirms the file parses well enough to be picked up) plus a visual pass with `rtk read .github/workflows/live-eval.yml` — indentation is the usual failure mode.
Expected: `live-eval` listed; file reads as intended. The real proof is Task 4's run. Report to user; user commits.

---

### Task 4: Run the live eval, collect the numbers

**Files:**
- No repo edits. Produces the three held-out metrics (+ a fresh tuning-set confirmation) that Task 5 writes into the README.

**Interfaces:**
- Consumes: the merged workflow from Task 3 on `main` (needs Tasks 1-3 committed and pushed by the user).
- Produces: actual held-out numbers for the default pair — `answerable resolved/16`, `escalated/4`, proxy, citation Jaccard — plus run date. Never publish scripted or estimated numbers.

- [ ] **Step 1: Trigger the run**

Run: `rtk gh workflow run live-eval.yml --ref main`
(Requires `CS_AGENT_MODEL_KEY` already configured as a repo secret — it is; the last publish run used the live pipeline.)

- [ ] **Step 2: Wait for completion and read the summary**

Run: `rtk gh run watch` (pick the `live-eval` run) or poll `rtk gh run list --workflow live-eval.yml --limit 1`.
Expected: job goes green (both sets pass). If either FAILs — that is the honest-fail gate doing its job. Do NOT touch the fixture or prompt inside this task; report the MISS lines to the user, who decides: accept and record the miss (README carries the honest numbers), or invoke the one-shot rule (fix prompt/model, then replace each missed question with a fresh one — authored against the corpus, loader invariants intact — and rerun).

- [ ] **Step 3: Record the numbers verbatim** from the job summary (both blocks): counts, rates, Jaccard, proxy, and the run date. Task 5 uses them.

---

### Task 5: README + docs land the disclosure

**Files:**
- Modify: `README.md` (eval section, ~lines 94-126, and the Known limitations list ~line 166)
- Modify: `CLAUDE.md` (Known limitations item 4)
- Modify: `docs/designs/design-2026-09-05.md` (In-sample disclosure paragraph in the fixture appendix)
- Modify: `docs/designs/TODOS.md` (new DONE entry)

**Interfaces:**
- Consumes: the numbers + run date from Task 4.
- Produces: shipped disclosure — in-sample caveat retired, held-out table published, one-shot rule stated, docs synced.

- [ ] **Step 1: README eval section.** Read lines 94-126 first, then:

After the existing per-pair table (ends ~line 110), insert a held-out subsection — substitute Task 4's real numbers for the `NN` placeholders, this is the one place the plan cannot pre-know the values:

```markdown
### Held-out set (generalization check)

A second fixture of 20 questions (`fixtures/questions-heldout.jsonl` — 16
answerable, 4 unanswerable) was authored after the verifier prompts were
locked, and never influenced them. Run with `cs-agent eval --heldout`.

| Draft + verify pair | Answerable | Unanswerable | Proxy | Citation Jaccard |
|---|---|---|---|---|
| deepseek-v4-flash-0731 (both roles) *(default)* | NN/16 | N/4 | N | 0.NN |

One live run, YYYY-MM-DD. **One-shot rule:** if a future change misses a
held-out question, fix the prompt or model and REPLACE that question with a
fresh one — a question tuned against its own miss is in-sample from that
moment. The gate for both sets is the same honest-fail exit code.
```

Rewrite the in-sample caveat (lines ~114-115) from:

```markdown
- **In-sample eval.** The same 25 questions tune the prompts and produce these
  numbers. A held-out set is the first upgrade once the fixture grows.
```

to:

```markdown
- **Tuning set is in-sample.** The 25-question table above is tuning-set
  scores — the prompts were iterated against those exact questions. The
  held-out table measures generalization; the tuning table measures fit.
```

Update the Known limitations entry (~line 166) `4. **In-sample eval** — see caveats above.` to:

```markdown
4. **Tuning-set numbers are in-sample** — generalization is measured by the
   held-out set above, which stays one-shot (missed questions get replaced,
   not re-tuned).
```

- [ ] **Step 2: CLAUDE.md Known limitations item 4.** Read the file, then replace:

```markdown
4. **In-sample eval** — same 25 questions tune the prompts and produce the
   published numbers; README discloses. Upgrade trigger: add a held-out set
   when fixture grows.
```

with:

```markdown
4. **Tuning-set numbers in-sample** — the 25-question table is tuning-set fit;
   the held-out set (20 questions, `eval --heldout`) carries the
   generalization claim and is one-shot (missed questions replaced, not
   re-tuned). README carries both.
```

- [ ] **Step 3: Design doc in-sample disclosure paragraph.** Append one sentence to the end of the "In-sample disclosure (added by outside voice, 2026-09-05)" paragraph in the fixture appendix:

```markdown
*(Changed 2026-09-06: a 20-question held-out fixture (`fixtures/questions-heldout.jsonl`, `eval --heldout`) now carries the published generalization numbers; the tuning-set numbers remain fit-to-tuning-set, disclosed as such in the README.)*
```

- [ ] **Step 4: TODOS.md DONE entry.** Append, following the existing DONE-entry pattern (the CI eval gate entry above it):

```markdown
## DONE: Held-out eval set (closed 2026-09-06; was README/D-design-doc "first upgrade")

**What shipped:** `fixtures/questions-heldout.jsonl` — 20 fresh questions
(16 answerable across 14 pages, 4 unanswerable) authored against the same
20-page corpus after the verifier prompts were locked. `cs-agent eval
--heldout` scores it through the identical pipeline and gate (exit 0/1,
targets scale: ≥80% of 16 answerable, 4/4 unanswerable, proxy 0). CI stub
gate covers it (`EvalGateTests.ScriptedHeldOut_MeetsAllPassTargets` —
harness regressions only, same honest scope as the tuning gate). The
`live-eval` workflow runs both sets and posts both summaries.

**One-shot rule:** a held-out miss means fix prompt/model, then REPLACE the
missed question with a fresh one. Never re-tune against the miss and re-score
the same question as held-out. The rule is stated next to the README numbers.
```

- [ ] **Step 5: Verify consistency** — grep for stale wording:

Run: `rtk grep -n "in-sample" README.md CLAUDE.md docs/designs/design-2026-09-05.md docs/designs/TODOS.md`
Expected: every remaining hit reads as the new tuning-set-vs-held-out framing; no hit claims the published numbers are in-sample without the held-out counterpart.
Then `rtk dotnet test -c Release` once more (docs-only, but cheap insurance).
Expected: all PASS. Report to user; user commits.

---

## Self-Review

**Spec coverage:** In-sample caveat (README + design doc + CLAUDE.md limitation 4) → Tasks 1-5 retire it with a real held-out set and published numbers. Fixture schema/scoring/targets → unchanged core, parametric `Passes`, verified by Task 1's scripted gate. Eval exit-code gate on both sets → Task 2 (CLI) + Task 3 (workflow gate step). One-shot rule → Global Constraints + Task 5 README/TODOS wording. Deferred-queue documentation → Task 5's TODOS DONE entry. CI wiring decision → stub gate covers the held-out fixture (harness regressions only); live eval stays manual, matching the D2 honest-scope decision. No gaps.

**Placeholder scan:** The only `NN` placeholders are Task 5's README table cells — they hold live-run metrics that cannot be known before Task 4 runs; the task step explicitly substitutes them and forbids publishing scripted numbers. No other TBD/TODO/"add appropriate X" patterns; every code step carries its exact code.

**Type consistency:** `RunScripted(escalateFlips, fixtureFile)` signature matches both call sites (existing tests pass no `fixtureFile`; the held-out test passes `"questions-heldout.jsonl"`). CLI flag `--heldout` spelled identically in Task 2 (CLI), Task 3 (workflow), and Task 5 (docs). Results dir `./eval-results/heldout` in Task 2 matches the gitignore analysis (covered by the `eval-results/` rule). `EvalScoring.Passes(metrics)` used unchanged in all three surfaces.