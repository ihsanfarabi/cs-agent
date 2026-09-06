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
- **One result object, three surfaces.** `answer · citations · claims[] · calls ·
  seconds · estimated_cost` live in one object; the CLI renders it (plus `--json`),
  the MCP server exposes the fields, and eval scores it.

## Quickstart

```bash
# install from source (NuGet publish pending)
git clone https://github.com/ihsanfarabi/cs-agent.git && cd cs-agent
dotnet tool install -g cs-agent --add-source <packed nupkg dir>   # or: dotnet run --project src/CsAgent.Cli

# 1. set your provider (any OpenAI-compatible endpoint; defaults use OpenRouter)
export CS_AGENT_MODEL_KEY=sk-or-...
export CS_AGENT_BASE_URL=https://openrouter.ai/api/v1

# 2. ingest your docs (local .md/.html; fixtures/ is the built-in eval corpus)
cs-agent ingest ./your-docs

# 3. ask — every answer is verified claim-by-claim before it prints
cs-agent ask "How do I rotate my API key?"
cs-agent ask --json "What is your SLA?"    # machine-readable result object

# 4. score the engine against the built-in fixture (exit 0 = pass gate)
cs-agent eval
```

Default models (eval-gated pair, see below): `deepseek/deepseek-v4-flash-0731`
drafts, `gpt-4o-mini` verifies. Override with `CS_AGENT_DRAFT_MODEL` /
`CS_AGENT_VERIFY_MODEL` for any OpenAI-compatible provider. Embeddings default to
`text-embedding-3-small` (OpenRouter serves them too).

Configuration is env vars only — `CS_AGENT_MODEL_KEY` is the only required one.
The full list lives in `src/CsAgent.Core/CsAgentConfig.cs`.

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
| deepseek-v4-flash-0731 + gpt-4o-mini *(default)* | 20/20 | 5/5 | 0 | 0.93 |
| deepseek-v4-flash-0731 (both roles) | 20/20 | 5/5 | 0 | 0.95 |
| z-ai/glm-5.3-flash (both roles) | 20/20 | 5/5 | 0 | 0.93 |
| qwen/qwen3.8-flash (both roles) | 20/20 | 5/5 | 0 | 0.95 |
| inclusionai/ling-3.0-flash-fin (both roles) | 19/20 | 5/5 | 0 | 0.88 |

Caveats, stated plainly:

- **In-sample eval.** The same 25 questions tune the prompts and produce these
  numbers. A held-out set is the first upgrade once the fixture grows.
- **Single-run pairs.** The gpt-4o pair held across six fresh eval runs; the
  cheaper pairs have one or two runs each. OpenRouter does not guarantee
  temperature-0 determinism, and one qwen run showed a single-question flake
  that a resume run did not reproduce.
- **Hallucination proxy undercounts.** It counts zero-overlap resolutions only;
  a wrong answer that happens to cite the right page passes it by design. It is
  reproducible and independent of the live verifier, which is why it exists.

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
4. **In-sample eval** — see caveats above.

Deferred work: a CI eval gate (GitHub Actions running `eval` per PR), URL
crawling for ingest, an HTTP API, Docker packaging, Postgres storage, and
escalation-with-actions (the escalation path gaining MAF tool-calling so a
"cannot answer" can open a ticket or notify a human).

## License

MIT — see [LICENSE](LICENSE).