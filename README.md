# cs-agent

A verifier-gated support resolution engine — ask a question about your own docs,
get an answer with page citations, and escalate anything the docs cannot verify.

Built on [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) (MAF) for .NET.
Bring your own model key (any OpenAI-compatible endpoint — OpenRouter works out of the box).

```
$ cs-agent ask "How do I rotate the API key?"
✓ resolved (4 claims, 4 supported)

Rotate a key from the dashboard: Settings → API keys → Rotate [1]. Rotation
generates a new key immediately, and the old key keeps working for 24 hours...

Sources:
[1] api-keys.md — API keys — ## Rotating keys

2 model calls · 6.7s
```

Unanswerable questions never get a hallucinated answer:

```
$ cs-agent ask "What is your SLA uptime percentage?"
✗ escalate — cannot answer from ingested docs
```

## How it works

```
ingest ──► retrieve top-k ──► draft (cited) ──► verify (ONE model call) ──► verdict
   store        embeddings        buffered          claim-by-claim          ▲ print only
                                                                    resolve │ if every claim
                                                                    escalate │ is supported
```

- **Verdict before output.** The draft is buffered. Nothing prints — on the CLI,
  MCP, or DevUI — until the verifier's resolve/escalate verdict exists.
- **The verifier is one model call** with a single prompt containing two labeled
  sections (claim extraction, support judgment), returning structured claims JSON.
  Worst case: 3 chat calls per question (draft + verify, plus one retry if the
  verifier emits malformed JSON twice).
- **Escalate is the safe default.** Any unsupported claim, zero claims, or
  malformed verifier JSON twice → escalate, with a `missing[]` payload naming what
  the docs lack. A rejected draft is never printed.
- **One result object, every surface.** `answer · citations · claims[] · calls ·
  seconds · estimated_cost` live in one object; the CLI renders it (plus `--json`),
  the HTTP poll returns it byte-identical, the MCP server exposes the fields, and
  eval scores it.

## Quickstart

```bash
# install from source (NuGet publish pending)
git clone https://github.com/ihsanfarabi/cs-agent.git && cd cs-agent
dotnet tool install -g cs-agent --add-source <packed nupkg dir>   # or: dotnet run --project src/CsAgent.Cli

# 1. set your key (any OpenAI-compatible endpoint; defaults use OpenRouter)
export CS_AGENT_MODEL_KEY=sk-or-...
export CS_AGENT_BASE_URL=https://your-endpoint/v1   # optional — only if NOT OpenRouter

# 2. ingest your docs (local .md/.html path, or crawl a docs site by URL;
#    fixtures/ is the built-in eval corpus)
cs-agent ingest ./your-docs
cs-agent ingest https://docs.example.com

# 3. ask — every answer is verified claim-by-claim before it prints
cs-agent ask "How do I rotate my API key?"
cs-agent ask --json "What is your SLA?"    # machine-readable result object

# 4. score the engine against the built-in fixture (exit 0 = pass gate)
cs-agent eval

# 5. (optional) expose the same engine over loopback HTTP
cs-agent serve
```

Default models (eval-gated pair, see below): `deepseek/deepseek-v4-flash-0731`
in both draft and verify roles. Override with `CS_AGENT_DRAFT_MODEL` /
`CS_AGENT_VERIFY_MODEL` for any OpenAI-compatible provider. Embeddings default to
`text-embedding-3-small` (OpenRouter serves them too).

Configuration is env vars only — `CS_AGENT_MODEL_KEY` is the only required one.
The full list lives in `src/CsAgent.Core/CsAgentConfig.cs`.

### URL ingest mode

`cs-agent ingest https://docs.example.com` crawls the site's HTML and ingests
markdown-converted content into a host-derived corpus (`docs.example.com` →
`cs-agent-docs-example-com.db`). Same-domain only, ≤ 200 pages, depth ≤ 3,
≥ 1 request/second, HTML only (JavaScript-rendered sites are out of scope in
v1). robots.txt is fetched and obeyed — `User-agent: *` plus an explicit
`cs-agent` group, prefix `Disallow` matching (no wildcards or `Allow` in v1);
an absent robots.txt allows, a 5xx robots.txt fails closed with no fetches.
`<meta name="robots" content="noindex">` pages are skipped.

Re-running the same URL resumes at the pipeline level: the crawl re-walks the
site's links (HTTP fetch, same 1 req/s pace), and pages whose content hash is
unchanged are skipped without re-embedding — a killed ingest continues at the
unvisited pages. Changed page content is picked up automatically by the same
hash check; deleting the corpus `.db` is only needed to re-embed from scratch.

**Trust boundary:** chunk text is untrusted input, doubly so for crawled
corpora. Prompts delimit chunk data and instruct the models to treat it as
data, never instructions — hardening makes corpus poisoning harder, not
impossible. A fully malicious corpus can still steer retrieval and force
escalations.

## The result object

One shape on every surface — CLI `--json`, MCP tool output, eval records:

```json
{
  "question": "…", "resolved": true, "answer": "…", "missing": null,
  "claims": [{ "claim": "…", "supported": true, "supporting_chunk_ids": [1] }],
  "citations": [{ "number": 1, "page_path": "docs/api-keys.md", "ordinal": 0, "text": "…", "score": 0.49 }],
  "calls": 2, "seconds": 6.7, "estimated_cost": "— (non-default model)"
}
```

## Eval results (honest numbers)

The built-in fixture is 25 synthetic questions over a 20-page corpus (20
answerable, 5 unanswerable — SLA, phone support, Terraform, upload limits,
crypto payment). Metrics: answerable resolution rate, page-level citation
Jaccard (cited = chunks the claims actually reference), and a hallucination
proxy (resolved with zero page overlap). Gate: ≥80% answerable resolved, 5/5
unanswerable escalated, proxy 0 — `eval` exits 1 on a miss (honest-fail).

| Draft + verify pair | Answerable | Unanswerable | Proxy | Citation Jaccard |
|---|---|---|---|---|
| gpt-4o + gpt-4o-mini | 20/20 | 5/5 | 0 | 0.95 |
| deepseek-v4-flash-0731 + gpt-4o-mini | 18–20/20 | 5/5 | 0 | 0.91–0.93 |
| deepseek-v4-flash-0731 (both roles) *(default)* | 17–20/20 | 5/5 | 0 | 0.89–0.93 |
| z-ai/glm-5.3-flash (both roles) | 20/20 | 5/5 | 0 | 0.93 |
| qwen/qwen3.8-flash (both roles) | 20/20 | 5/5 | 0 | 0.95 |
| inclusionai/ling-3.0-flash-fin (both roles) | 19/20 | 5/5 | 0 | 0.88 |

### Held-out set (generalization check)

A second fixture of 20 questions (`fixtures/questions-heldout.jsonl` — 16
answerable, 4 unanswerable) was authored after the verifier prompts were
locked, and never influenced them. Run with `cs-agent eval --heldout`.

| Draft + verify pair | Answerable | Unanswerable | Proxy | Citation Jaccard |
|---|---|---|---|---|
| deepseek-v4-flash-0731 (both roles) *(default)* | 15–16/16 | 4/4 | 0 | 0.91–0.97 |

Two live runs, 2026-09-06 (the `live-eval` workflow, which gates both sets):
16/16 with the original prompts, 15/16 with the prompt hardening shipped in
the URL-crawl release. A local run of the same pair also scored 15/16 with one
sampling-variance miss — the same non-determinism the caveat below records.
**One-shot rule:** if a future change misses a held-out question, fix the
prompt or model and REPLACE that question with a fresh one — a question tuned
against its own miss is in-sample from that moment. The gate for both sets is
the same honest-fail exit code.

Caveats, stated plainly:

- **Tuning set is in-sample.** The 25-question table above is tuning-set
  scores — the prompts were iterated against those exact questions. The
  held-out table measures generalization; the tuning table measures fit.
- **Single-run pairs.** The gpt-4o pair held across six fresh eval runs; the
  cheaper pairs have one or two runs each. OpenRouter does not guarantee
  temperature-0 determinism, and one qwen run showed a single-question flake
  that a resume run did not reproduce. The deepseek + gpt-4o-mini pair's third
  fresh run scored 18/20 — both misses escalated correctly-cited borderline
  answers and resolved on immediate re-ask (sampling variance, not a model
  defect); that variance is why the default moved to all-deepseek, which has
  scored 17–20/20 across three live runs — every miss escalated instead of
  hallucinating, and every run stayed above the 80% gate.
- **Hallucination proxy undercounts.** It counts zero-overlap resolutions only;
  a wrong answer that happens to cite the right page passes it by design. It is
  reproducible and independent of the live verifier, which is why it exists.

## HTTP API

`cs-agent serve` starts a loopback-only HTTP server exposing the same result
object (default port 5123; `--port N` or `CS_AGENT_PORT` overrides it).
Asks are slow — p95 31.3s measured over 45 live asks on the default pair,
2026-09-06 — so `/ask` never blocks: it returns `202` plus a job id, and the
client polls:

```bash
$ cs-agent serve
listening on http://127.0.0.1:5123 — POST /ask · GET /result/{id} · GET /health · /openapi/v1.json

# 1. submit
$ curl -s http://127.0.0.1:5123/ask -H 'Content-Type: application/json' \
    -d '{"question": "How do I rotate the API key?"}'
{"id":"e2e19695…","status":"running","result":"/result/e2e19695…"}

# 2. poll until status leaves "running"
$ curl -s http://127.0.0.1:5123/result/e2e19695…
{ "question": "How do I rotate the API key?", "resolved": true, "answer": "…", … }

# 3. optional trace bundle — audit an escalation over curl: the draft the
#    verifier rejected, plus the raw verifier JSON (result untouched)
$ curl -s "http://127.0.0.1:5123/result/e2e19695…?evidence=true"
{
  "result": { "question": "…", "resolved": false, "answer": null, "missing": [ … ], … },
  "evidence": { "draft_answer": "…", "draft_rejected": true, "verifier_raw": "[ … ]" }
}
```

- **Done → 200** with the canonical `AskResult` JSON — byte-identical to
  `cs-agent ask --json` for the same question (one result object, one shared
  serializer declaration). An escalation is a 200 done with `resolved:false`
  and `missing[]` populated, never an error.
- **Errored → the recorded 5xx**, replayed on every poll: RFC 9457
  ProblemDetails with stable `type` URNs — `urn:cs-agent:store:empty-store`
  → 503, `urn:cs-agent:model:transport` → 502 when a model call cannot reach
  the provider (matched on exception type at any wrap depth, including the
  OpenAI SDK's bare `ClientResultException`). No stack trace ever reaches a
  response body.
- **Unknown id → 404.** Jobs live in process memory — a restart loses every
  id; resubmit to recover (documented v1 cut: no persistence, no eviction, no
  DELETE endpoint).
- **`?evidence=true` on a done poll** returns the canonical result untouched
  plus an `evidence` trace: `draft_answer` as written, `draft_rejected`, and
  `verifier_raw` — the verifier's raw JSON output (null when its model call
  failed to reach the provider). The rejected draft appears ONLY inside
  `evidence`, never in the canonical result. Running/errored/unknown polls
  ignore the flag.
- Asks run **one at a time** behind a single pipeline; concurrent submits
  queue. `GET /health` answers 200 without touching the store or a model.
- The server binds `127.0.0.1` by default — it is explicitly a no-auth,
  no-rate-limit local tool. `CS_AGENT_BIND` (a single IP address, no
  hostnames) widens the bind for the container/reverse-proxy case and
  prints a warning; auth and rate limiting wait for later work.

`serve` fails fast at startup with a named structured error and exit 1 — on a
bad port, a bad bind value, a missing store path (checked before
construction, so a refusal leaves no stray empty `.db` behind), a corrupt
store, an embedding-model mismatch, or an empty store (no documents
ingested). The OpenAPI document lives at `/openapi/v1.json`.

## Docker

The same engine ships as a container image — no .NET install, no tool path.
The image is the full CLI: every subcommand works, and the store lives on a
`/data` volume so ingest and serve share it across runs.

```bash
# 1. ingest (into a named volume; local docs must be mounted, URLs crawl from the container)
docker run --rm -v cs-agent-data:/data -v "$PWD/docs:/docs:ro" \
    -e CS_AGENT_MODEL_KEY=sk-or-... \
    ghcr.io/ihsanfarabi/cs-agent ingest /docs

# 2. serve — image bakes CS_AGENT_BIND=0.0.0.0; publishing to 127.0.0.1 keeps
#    the host-side exposure local (the safe form — see the note below)
docker run -d --name cs-agent -p 127.0.0.1:5123:5123 -v cs-agent-data:/data \
    -e CS_AGENT_MODEL_KEY=sk-or-... \
    ghcr.io/ihsanfarabi/cs-agent serve
curl -s http://localhost:5123/health

# 3. ask (same poll flow as above; add ?evidence=true to audit escalations)
curl -s http://localhost:5123/ask -H 'Content-Type: application/json' \
    -d '{"question": "How do I rotate the API key?"}'
```

The image binds `0.0.0.0` (container default) — it is still a no-auth,
no-rate-limit API: the examples above publish the port to localhost only
(`-p 127.0.0.1:5123:5123`); for anything wider, put it behind a trusted
proxy. Named volumes
(the examples above) just work; if you bind-mount a host directory as
`/data`, it is root-owned inside the container and the non-root app user
cannot write the store — `chown` it to the container user or run with
`--user`. There is no HEALTHCHECK directive in the image; orchestrators
should probe `GET /health`. The MCP stdio server is not in the image — run
it from source per the MCP section below, so MCP clients never go through
the container. Job memory, restart semantics, and all other
HTTP limitations above apply unchanged.

## MCP server

`src/CsAgent.Mcp` is an MCP stdio server exposing `ask` and `ingest` tools with
the same result-object contract. Point any MCP client at:

```bash
dotnet run --project src/CsAgent.Mcp
```

Logging goes to stderr only — one stdout line corrupts the stdio protocol.

## Learning Microsoft Agent Framework from this repo

cs-agent is small enough to read end-to-end, and it exercises the MAF APIs that
matter for real console agents:

- `ChatClientAgent` + `AsAIAgent(name:, instructions:)` — the core agent seam
  (`src/CsAgent.Core/CsAgentRuntime.cs`)
- `RunAsync<T>` structured output for the verifier's claims JSON
- A `DeterministicChatClient` `IChatClient` wrapper that pins temperature 0 on
  every call — the agent-level wrap is the reliable hook, not per-call options
- A handoff-graph stub with DevUI hosting under `prototype/` (trace recording
  pending — run `dotnet run --project prototype/stub-handoff -- devui`)

Related MAF samples reviewed while building this (links verified September 2026):
[tsjdev-apps/maf-demo-console](https://github.com/tsjdev-apps/maf-demo-console),
[leestott/On-Call-Copilot-Multi-Agent](https://github.com/leestott/On-Call-Copilot-Multi-Agent),
[UtsavOpal/ragwarden](https://github.com/UtsavOpal/ragwarden). As of September
2026, among the MAF samples reviewed, cs-agent is the only one that is also an
installable product with an eval harness.

## Known limitations (intentional v1 cuts)

1. **Static price table** — `estimated_cost` is an estimate against a static
   table and prints `— (non-default model)` unless both models are priced; no
   per-call token usage is captured yet.
2. **Plain top-k search** — no reranking or hybrid retrieval.
3. **Hallucination proxy undercounts** — see caveats above.
4. **Tuning-set numbers are in-sample** — generalization is measured by the
   held-out set above, which stays one-shot (missed questions get replaced,
   not re-tuned).
5. **HTTP jobs are in-process memory** — `serve` loses all job ids on
   restart (poll → 404, resubmit to recover); no eviction, TTL, or DELETE.
   Persistence waits for the Postgres work.

Deferred work: Postgres storage and
escalation-with-actions (the escalation path gaining MAF tool-calling so a
"cannot answer" can open a ticket or notify a human).

## License

MIT — see [LICENSE](LICENSE).