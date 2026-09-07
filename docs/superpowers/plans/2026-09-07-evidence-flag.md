# Evidence Flag (`?evidence=true`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional `?evidence=true` trace bundle to the HTTP API's done poll (`GET /result/{id}?evidence=true`) — the rejected draft and raw verifier JSON alongside the untouched canonical result, making escalations auditable over curl.

**Architecture:** The ask pipeline gains a second public entry point, `RunWithEvidence`, that returns the canonical `AskResult` plus a new `AskEvidence` record (draft as written, rejected flag, raw verifier output). `AskResult` itself — the one-result-object contract rendered by CLI/MCP/eval — is untouched. The HTTP job store carries the evidence alongside the result, and the done poll returns a `{ result, evidence }` envelope only when the query flag is set. Without the flag, every byte of the existing contract is unchanged (pinned by the existing byte-identical test).

**Tech Stack:** C#/.NET 10, ASP.NET Core minimal APIs (Kestrel), xUnit on the fake-or-real model seam.

**Spec:** `docs/designs/TODOS.md` OPEN entry "evidence flag on POST /ask — `?evidence=true` trace bundle (deferred 2026-09-07, HTTP-API eng review D9)" + `docs/designs/design-2026-09-07-http-api.md` "Approach C: evidence flag (lateral, declined — deferred as a future surface increment)". This plan is that surface increment.

## Global Constraints

- Verifier = ONE model call (worst case 3 LLM calls per ask: draft + verify + one malformed retry). Evidence capture must not add model calls.
- Verdict before output: evidence exists only after the resolve/escalate verdict does — never streamed, never polled before `Done`.
- One result object, three surfaces: `AskResult` fields and their JSON names do not change. Evidence is a SEPARATE record/envelope; the canonical bare poll stays byte-identical.
- A rejected draft never surfaces in the canonical result (`answer` stays null on escalation). Under `?evidence=true` it appears ONLY inside `evidence`, explicitly marked `draft_rejected: true`.
- Escalate stays the safe default; `RunWithEvidence` never fabricates evidence — if the verifier transport threw, `verifier_raw` is null (honest), not reconstructed.
- Validate loudly, no silent wrong answers; no stack traces in response bodies.
- Env vars only; no new config surface for this feature (the flag is per-request query parameter).
- xUnit via the fake seam; commits in this repo carry no co-author trailer.

**Proven fact (do not re-derive):** MAF's `AgentResponse<T>` from `verify.RunAsync<IReadOnlyList<ClaimVerdict>>(...)` exposes `.Text` (raw string) alongside `.Result`. Verified by reflection AND a live probe against CsAgent.Core with a fake `IChatClient`: `Text` returned exactly the JSON string the fake emitted. Malformed-twice and transport-throw cases were traced from the existing `VerifyOnce` code.

---

### Task 1: Core — `AskEvidence` record + `RunWithEvidence` + raw verifier capture

**Files:**
- Modify: `src/CsAgent.Core/AskResult.cs` (AskResult.cs holds both the record and the pipeline — follow the existing layout)
- Test: `tests/CsAgent.Tests/AskPipelineTests.cs`

**Interfaces:**
- Consumes: existing `AskPipeline` internals (`VerifyOnce`, `FormatDraftPrompt`, `VerifierRule`) — unchanged behavior.
- Produces (Task 2 depends on these EXACT names):
  - `public sealed record AskEvidence(string DraftAnswer, bool DraftRejected, string? VerifierRaw)` — JSON names `draft_answer`, `draft_rejected`, `verifier_raw`
  - `public sealed record AskRun(AskResult Result, AskEvidence Evidence)`
  - `public AskRun RunWithEvidence(string question, CancellationToken cancellationToken = default)` on `AskPipeline`
  - `public AskResult Run(...)` — unchanged signature, now delegates to `RunWithEvidence(...).Result`

- [ ] **Step 1: Write the failing tests**

Append to `tests/CsAgent.Tests/AskPipelineTests.cs` (inside the class — the file already has `MakePipeline(draftAnswer, verifyJson, verifyJsonRetry)` seeding two chunks under `docs/api-keys.md`, plus the constants `ResolvedJson` and `EscalateJson`):

```csharp
    [Fact]
    public void RunWithEvidence_Resolved_CapturesDraftAndRawVerifierJson()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]",
            ResolvedJson, ResolvedJson);

        var run = pipeline.RunWithEvidence("How do I rotate the API key?");
        Assert.True(run.Result.Resolved);
        Assert.False(run.Evidence.DraftRejected);
        Assert.Equal(run.Result.Answer, run.Evidence.DraftAnswer); // accepted draft, identical
        Assert.Equal(ResolvedJson, run.Evidence.VerifierRaw);       // raw verifier output, exact
    }

    [Fact]
    public void RunWithEvidence_Escalated_RejectedDraftAndRawCaptured()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.",
            EscalateJson, EscalateJson);

        var run = pipeline.RunWithEvidence("What is your SLA uptime?");
        Assert.False(run.Result.Resolved);
        Assert.Null(run.Result.Answer); // canonical surface keeps the rejection hidden
        Assert.Equal("Rotate from Settings [1]. SLA uptime is 99.9%.", run.Evidence.DraftAnswer);
        Assert.True(run.Evidence.DraftRejected); // the trace labels it gated, explicitly
        Assert.Equal(EscalateJson, run.Evidence.VerifierRaw);
    }

    [Fact]
    public void RunWithEvidence_MalformedTwice_RawIsLastVerifierOutput()
    {
        var (pipeline, _, _) = MakePipeline(
            "Rotate from Settings [1].", "THIS IS NOT JSON", "STILL NOT JSON");

        var run = pipeline.RunWithEvidence("How do I rotate keys?");
        Assert.False(run.Result.Resolved);
        Assert.Equal(3, run.Result.Calls); // draft + 2 verify attempts — no new calls
        Assert.True(run.Evidence.DraftRejected);
        // the trace shows WHAT was malformed — that is the audit value
        Assert.Equal("STILL NOT JSON", run.Evidence.VerifierRaw);
    }

    [Fact]
    public void RunWithEvidence_VerifierTransportThrows_RawNullStillEscalates()
    {
        // verify transport failures fail closed (existing behavior); no response
        // object existed, so raw is null — never reconstructed (honest evidence)
        SeedStore();
        var retriever = new Retriever(
            new FakeEmbeddingGenerator(), new SqliteVectorStore(_dbPath, "fake-embedding"), topK: 5);
        ChatClientAgent Draft() => new(new FakeChatClient(_ => "Rotate from Settings [1]."));
        ChatClientAgent Verify() => new(new FakeChatClient(
            _ => throw new HttpRequestException("connection refused")));
        var pipeline = new AskPipeline(Draft, Verify, retriever);

        var run = pipeline.RunWithEvidence("How do I rotate keys?");
        Assert.False(run.Result.Resolved);
        Assert.True(run.Evidence.DraftRejected);
        Assert.Null(run.Evidence.VerifierRaw);
    }
```

The file's usings (`System.Net.Http` for `HttpRequestException` comes from implicit usings in the test project; if the build says otherwise, add `using System.Net.Http;`) — check the top of the file if the transport test does not compile.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AskPipelineTests"`
Expected: COMPILE ERROR — `AskPipeline` has no `RunWithEvidence`, no `AskEvidence`/`AskRun` types. That is the failure.

- [ ] **Step 3: Write the implementation**

In `src/CsAgent.Core/AskResult.cs`:

(a) Add the two records directly below `AskResult` (before the `AskPipeline` class), matching the file's JSON-name pinning style:

```csharp
/// <summary>
/// The optional audit trace for one completed ask. Retrieved chunks and
/// per-claim support already live in AskResult (citations/claims); this adds
/// what no surface prints: the draft as written — even when the verifier
/// rejected it — and the raw verifier JSON. Never fabricated: a transport
/// failure leaves verifier_raw null.
/// </summary>
public sealed record AskEvidence(
    [property: JsonPropertyName("draft_answer")] string DraftAnswer,
    [property: JsonPropertyName("draft_rejected")] bool DraftRejected,
    [property: JsonPropertyName("verifier_raw")] string? VerifierRaw);

/// <summary>One completed ask: the canonical result plus its evidence trace.</summary>
public sealed record AskRun(AskResult Result, AskEvidence Evidence);
```

(b) Replace the body of `Run` + `VerifyOnce` with the evidence-capturing versions (retrieval/draft/verify logic identical — only the return plumbing changes):

```csharp
    public AskResult Run(string question, CancellationToken cancellationToken = default)
        => RunWithEvidence(question, cancellationToken).Result;

    /// <summary>
    /// Run returning the canonical result plus the evidence trace. AskResult
    /// alone stays the contract every surface renders — Run keeps that shape
    /// for CLI/MCP/eval; only the HTTP evidence surface reads this method.
    /// </summary>
    public AskRun RunWithEvidence(string question, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("retrieving");
        var chunks = retriever.Retrieve(question, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("drafting");
        var draft = draftAgentFactory();
        var draftResponse = draft.RunAsync(
            FormatDraftPrompt(question, chunks), cancellationToken: cancellationToken).GetAwaiter().GetResult();
        var answer = draftResponse.Text.Trim();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("verifying");
        var (claims, verifierRaw) = VerifyOnce(question, answer, chunks, cancellationToken);

        stopwatch.Stop();

        AskResult result;
        if (claims is null)
            // second malformed verdict ⇒ fail closed to escalate
            result = new AskResult(question, false, null,
                [new MissingItem(question, "verifier output malformed twice — failing closed")],
                [], chunks, Calls: 3, stopwatch.Elapsed.TotalSeconds, CostLine());
        else if (VerifierRule.Resolve(claims))
            result = new AskResult(question, true, answer, null, claims, chunks, Calls: 2,
                stopwatch.Elapsed.TotalSeconds, CostLine());
        else
            result = new AskResult(question, false, null, VerifierRule.BuildMissing(claims, question),
                claims, chunks, Calls: 2, stopwatch.Elapsed.TotalSeconds, CostLine());

        return new AskRun(result, new AskEvidence(answer, !result.Resolved, verifierRaw));
    }

    private (IReadOnlyList<ClaimVerdict>? Claims, string? Raw) VerifyOnce(
        string question, string answer, IReadOnlyList<CitedChunk> chunks, CancellationToken cancellationToken)
    {
        var verify = verifyAgentFactory();
        var prompt = FormatVerifyPrompt(question, answer, chunks);
        string? raw = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = verify.RunAsync<IReadOnlyList<ClaimVerdict>>(
                    prompt, cancellationToken: cancellationToken).GetAwaiter().GetResult();
                raw = response.Text; // captured BEFORE the Result check — malformed text IS the evidence
                if (response.Result is not null)
                    return (response.Result, raw);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                // malformed structured output — the pipeline's ONLY retry.
                // Cancellation is exempt: shutdown propagates instead of
                // fabricating a fail-closed verdict (verify stays fail-closed
                // for every non-cancellation failure — unchanged).
            }
            prompt = prompt + "\n\nYour previous output was not valid JSON. Output ONLY the JSON array.";
        }
        return (null, raw); // caller treats null claims as fail-closed to escalate
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AskPipelineTests"`
Expected: PASS — 4 new tests plus all existing pipeline tests (Run's behavior is unchanged: `MalformedTwice_FailsClosedToEscalate`, `CancelledDuringVerify_PropagatesInsteadOfFailingClosed`, etc. must stay green).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: 114/114 PASS (110 existing + 4 new). The MCP/eval/CLI surfaces compile untouched — they call `Run`, whose signature did not change.

- [ ] **Step 6: Commit**

```bash
git add src/CsAgent.Core/AskResult.cs tests/CsAgent.Tests/AskPipelineTests.cs
git commit -m "feat: AskEvidence trace — RunWithEvidence captures rejected draft + raw verifier JSON"
```

---

### Task 2: HTTP — job store carries evidence, done poll gains `?evidence=true`

**Files:**
- Modify: `src/CsAgent.Http/AskJobStore.cs`
- Modify: `src/CsAgent.Http/MapCsAgentApi.cs`
- Test: `tests/CsAgent.Tests/HttpApiTests.cs`

**Interfaces:**
- Consumes (from Task 1): `pipeline.RunWithEvidence(question, ct)` returning `AskRun(AskResult Result, AskEvidence Evidence)`; `AskEvidence(DraftAnswer, DraftRejected, VerifierRaw)`.
- Produces:
  - `AskJob` record gains `AskEvidence? Evidence` (5th positional field, before `Error`)
  - HTTP contract: `GET /result/{id}?evidence=true` on a Done job returns `200` with `AskEvidenceEnvelope` = `{ "result": <canonical AskResult>, "evidence": { "draft_answer", "draft_rejected", "verifier_raw" } }`. All other states (running/errored/404) byte-identical to today, flag or not.

- [ ] **Step 1: Write the failing tests**

In `tests/CsAgent.Tests/HttpApiTests.cs`:

(a) Extend the poll helper to take a query suffix (existing call sites unchanged):

```csharp
    private static async Task<HttpResponseMessage> PollSettledAsync(
        HttpClient client, string id, int seconds = 30, string query = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/result/{id}{query}");
            // ... rest of the body UNCHANGED
```

(only the `GetAsync` line changes — replace `$"/result/{id}"` with `$"/result/{id}{query}"`).

(b) Append these tests inside the class:

```csharp
    [Fact]
    public async Task EvidencePoll_EnvelopeResultByteIdenticalToBarePoll()
    {
        using var client = await StartAsync(MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]", ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var done = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var bare = await done.Content.ReadAsStringAsync();

        // same job, evidence poll: canonical result nested, byte-identical
        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, withEvidence.StatusCode);
        var envelope = JsonSerializer.Deserialize<AskEvidenceEnvelope>(
            await withEvidence.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.True(envelope.Result.Resolved);
        Assert.Equal(bare, JsonSerializer.Serialize(envelope.Result, CsAgentJson.SerializerOptions));
        Assert.Equal(ResolvedJson, envelope.Evidence.VerifierRaw);
        Assert.False(envelope.Evidence.DraftRejected);
        Assert.Contains("Rotate keys", envelope.Evidence.DraftAnswer);
    }

    [Fact]
    public async Task EscalatedEvidencePoll_RejectedDraftGatedUnderEvidence()
    {
        const string draft = "Rotate from Settings [1]. SLA uptime is 99.9%.";
        using var client = await StartAsync(MakePipeline(draft, EscalateJson));

        var id = await SubmitAsync(client, "What is your SLA uptime?");
        using var done = await PollSettledAsync(client, id);
        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, withEvidence.StatusCode);

        var envelope = JsonSerializer.Deserialize<AskEvidenceEnvelope>(
            await withEvidence.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.False(envelope.Result.Resolved);
        Assert.Null(envelope.Result.Answer); // rejected draft never surfaces in the canonical result
        Assert.Equal(draft, envelope.Evidence.DraftAnswer); // ...only inside evidence, explicitly gated
        Assert.True(envelope.Evidence.DraftRejected);
        Assert.NotEmpty(envelope.Result.Missing!);
    }

    [Fact]
    public async Task EvidencePollWhileRunning_StillRunningShape()
    {
        // slow draft pins the running poll — evidence flag must not change the
        // running body (verdict-before-output: no trace exists before Done)
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => { Thread.Sleep(250); return "Rotate from Settings [1]."; },
            _ => ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var running = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        var body = await ReadJsonAsync(running);
        Assert.Equal("running", body.GetProperty("status").GetString());

        using var settled = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
    }

    [Fact]
    public async Task EvidencePollUnknownJob_404()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.GetAsync("/result/deadbeef?evidence=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("urn:cs-agent:http:unknown-job", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EvidencePollOnErroredJob_Replays5xxNoEnvelope()
    {
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => throw new HttpRequestException("connection refused"), _ => ResolvedJson));
        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var errored = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.BadGateway, errored.StatusCode);

        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.BadGateway, withEvidence.StatusCode); // replay, unchanged
        var body = await withEvidence.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:model:transport", body);
        Assert.DoesNotContain("\"evidence\"", body); // an errored job has no trace — no fabricated bundle
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~HttpApiTests"`
Expected: COMPILE ERROR — `AskEvidenceEnvelope` does not exist. (The running-poll and 404 tests would PASS against today's code — that is correct: they pin that the flag alone changes nothing. The two envelope tests are the real failures; after adding the type without wiring they fail with null deserialization.)

- [ ] **Step 3: Write the implementation**

(a) `src/CsAgent.Http/AskJobStore.cs` — the `AskJob` record gains the evidence field, and `Execute` uses `RunWithEvidence`:

```csharp
public sealed record AskJob(
    string Id,
    AskJobStatus Status,
    AskResult? Result,
    AskEvidence? Evidence,
    ProblemDetails? Error,
    int? ErrorStatus);
```

Update the three construction sites in the same file:

```csharp
        _jobs[id] = new AskJob(id, AskJobStatus.Running, null, null, null, null);
```

```csharp
            completed = new AskJob(id, AskJobStatus.Errored, null, null, problem, status);
```

```csharp
    private AskJob Execute(string id, string question)
    {
        try
        {
            // verdict-before-output holds: Done is only recorded after the
            // resolve/escalate verdict exists inside AskResult
            var run = _pipeline.RunWithEvidence(question, _shutdown);
            return new AskJob(id, AskJobStatus.Done, run.Result, run.Evidence, null, null);
        }
        catch (Exception ex)
        {
            var (status, problem) = ProblemMapping.Map(ex);
            return new AskJob(id, AskJobStatus.Errored, null, null, problem, status);
        }
    }
```

(b) `src/CsAgent.Http/MapCsAgentApi.cs`:

Add to the usings at the top:
```csharp
using System.Text.Json.Serialization;
```

Change the done-poll route (only the two marked lines differ from today):

```csharp
        app.MapGet("/result/{id}", (string id, bool evidence = false) =>
        {
            var job = store.Get(id);
            if (job is null)
                return Bad(404, "http", "unknown-job",
                    $"No ask job '{id}' — ids are in-memory and lost on restart. Resubmit to /ask.");

            return job.Status switch
            {
                AskJobStatus.Running => Results.Json(new { id = job.Id, status = "running" }),
                // CHANGED: done + ?evidence=true → envelope; bare poll byte-identical to before
                AskJobStatus.Done => evidence && job.Evidence is not null
                    ? Results.Json(new AskEvidenceEnvelope(job.Result!, job.Evidence), CsAgentJson.SerializerOptions)
                    : Results.Json(job.Result, CsAgentJson.SerializerOptions),
                AskJobStatus.Errored => Results.Json(job.Error, statusCode: job.ErrorStatus ?? 500),
                _ => Bad(500, "internal", "unmapped", null),
            };
        });
```

Add the envelope record next to `AskRequest` at the bottom of the file:

```csharp
/// <summary>
/// GET /result/{id}?evidence=true done body: the canonical AskResult untouched
/// (byte-identical to the bare poll) with the evidence trace alongside.
/// </summary>
public sealed record AskEvidenceEnvelope(
    [property: JsonPropertyName("result")] AskResult Result,
    [property: JsonPropertyName("evidence")] AskEvidence Evidence);
```

(The `evidence = false` parameter binds from the query string — standard minimal-API simple-type binding; absent flag, `?evidence=false`, and any other value all take the bare branch.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~HttpApiTests"`
Expected: PASS — 5 new + all 12 existing HTTP tests, including the untouched `SubmitAccepted_PollsToDone_ContractByteIdentical` (the bare contract did not move by one byte).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: 119/119 PASS (114 from Task 1 + 5 new).

- [ ] **Step 6: Commit**

```bash
git add src/CsAgent.Http/AskJobStore.cs src/CsAgent.Http/MapCsAgentApi.cs tests/CsAgent.Tests/HttpApiTests.cs
git commit -m "feat: GET /result/{id}?evidence=true — trace bundle (rejected draft + raw verifier JSON)"
```

---

### Task 3: Docs — README evidence curl, NuGet readme, TODOS DONE entry

**Files:**
- Modify: `README.md` (HTTP API section, lines ~180-222)
- Modify: `src/CsAgent.Cli/README.md` (serve quickstart block)
- Modify: `docs/designs/TODOS.md`

**Interfaces:**
- Consumes: the shipped feature from Tasks 1-2; nothing downstream consumes docs.
- Produces: published copy that matches the shipped contract.

- [ ] **Step 1: Root README — HTTP API section**

In `README.md`, inside the HTTP API section's bash block (after the `# 2. poll until status leaves "running"` example and its output line), append a third example:

```bash
# 3. optional trace bundle — audit an escalation over curl: the draft the
#    verifier rejected, plus the raw verifier JSON (result untouched)
$ curl -s "http://127.0.0.1:5123/result/e2e19695…?evidence=true"
{
  "result": { "question": "…", "resolved": false, "answer": null, "missing": [ … ], … },
  "evidence": { "draft_answer": "…", "draft_rejected": true, "verifier_raw": "[ … ]" }
}
```

And append one bullet to the existing bullet list in that section (after the "Unknown id → 404." bullet):

```markdown
- **`?evidence=true` on a done poll** returns the canonical result untouched
  plus an `evidence` trace: `draft_answer` as written, `draft_rejected`, and
  `verifier_raw` — the verifier's raw JSON output (null when its model call
  failed to reach the provider). The rejected draft appears ONLY inside
  `evidence`, never in the canonical result. Running/errored/unknown polls
  ignore the flag.
```

- [ ] **Step 2: NuGet readme — serve quickstart**

In `src/CsAgent.Cli/README.md`, extend the `# 5. serve…` block's poll comment:

Replace:
```bash
curl localhost:8080/result/{id}    # 200 running → 200 done (same result object)
```
with:
```bash
curl localhost:8080/result/{id}                # 200 running → 200 done (same result object)
curl "localhost:8080/result/{id}?evidence=true" # done + trace: rejected draft, raw verifier JSON
```

- [ ] **Step 3: TODOS — close the OPEN entry**

In `docs/designs/TODOS.md`, replace the whole "OPEN: evidence flag" section (lines 13-32) with:

```markdown
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
verdict-before-output holds: no trace exists before Done. Tests: 9 added
(4 pipeline-level, 5 endpoint-level incl. byte-identical nested result);
the bare-poll contract test still passes unmodified.

**Demo line:** escalations are now auditable over curl — poll with the
flag, read the rejected draft next to the verifier JSON that killed it.
```

(Keep the existing DONE sections below it unchanged.)

- [ ] **Step 4: Full suite + build one last time**

Run: `dotnet test`
Expected: 119/119 PASS.

- [ ] **Step 5: Commit**

```bash
git add README.md src/CsAgent.Cli/README.md docs/designs/TODOS.md
git commit -m "docs: ?evidence=true trace bundle — README curl examples, TODOS DONE entry"
```

---

## Self-Review (run during plan authoring — results)

1. **Spec coverage:** TODOS entry asks for (a) retrieved chunks — present via `result.citations` (the envelope nests the canonical result; TODOS's own contract note says the bundle "rides GET /result/{id}"), (b) raw verifier claims JSON — `verifier_raw` (Task 1), (c) per-claim support — present via `result.claims`, (d) rejected-draft content clearly labeled gated — `draft_answer` + `draft_rejected:true` inside `evidence` only (Tasks 1-2), (e) TODOS's re-shaped contract (`/result/{id}?evidence=true` on the done poll) — exactly what Task 2 builds. Approach C's two conditions (clear labeling of rejected-draft content; not re-opening the sync/async shape) both hold.
2. **Placeholder scan:** none — every step carries exact code or exact prose.
3. **Type consistency:** `AskEvidence(DraftAnswer, DraftRejected, VerifierRaw)` / `AskRun(Result, Evidence)` / `RunWithEvidence(question, ct)` defined in Task 1 are consumed verbatim in Task 2 (`run.Result`, `run.Evidence`). `AskJob` construction sites all updated (Running/Cancelled/Execute-error/Execute-done). JSON names `draft_answer`/`draft_rejected`/`verifier_raw`/`result`/`evidence` consistent across records, tests, and README copy.

## Notes for the executor

- The `Thread.Sleep(250)` in `EvidencePollWhileRunning_StillRunningShape` is the one timing-sensitive test; if it flakes on a loaded machine, raise the sleep, not the assert.
- MAF fact, already proven — do not burn a task re-deriving it: typed `RunAsync<T>` responses carry raw text in `.Text` (reflection + live probe against this exact Core project, 2026-09-07).
- The one-shot live eval gate is unaffected: no prompt text, no retrieval, no model call changed. If a live eval run is wanted for the release notes, run it after this merges, not as part of this plan.