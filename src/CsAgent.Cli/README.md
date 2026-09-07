# cs-agent CLI

Verifier-gated support resolution engine. Answer support questions from your own
docs — with page citations — and escalate anything the docs cannot verify.

```bash
# 1. set your provider (any OpenAI-compatible endpoint; defaults below use OpenRouter)
export CS_AGENT_MODEL_KEY=sk-or-...
export CS_AGENT_BASE_URL=https://openrouter.ai/api/v1
export CS_AGENT_EMBEDDING_MODEL=text-embedding-3-small     # default

# draft/verify defaults (eval-gated pair): DeepSeek in both roles.
# Override either for any OpenAI-compatible provider:
# export CS_AGENT_DRAFT_MODEL=deepseek/deepseek-v4-flash-0731
# export CS_AGENT_VERIFY_MODEL=deepseek/deepseek-v4-flash-0731

# 2. ingest your docs (local .md/.html path, or crawl a docs site by URL;
#    `fixtures` is the built-in eval corpus)
cs-agent ingest ./your-docs
cs-agent ingest https://docs.example.com   # same-domain HTML crawl, robots.txt obeyed

# 3. ask — every answer is verified claim-by-claim before it prints
cs-agent ask "How do I rotate my API key?"
cs-agent ask --json "What is your SLA?"   # machine-readable result object

# 4. score the engine against the built-in fixture (exit 0 = pass gate)
cs-agent eval

# 5. serve the engine over HTTP — async job queue (POST /ask → 202, poll result)
cs-agent serve --port 8080
curl -X POST localhost:8080/ask -H "content-type: application/json" \
  -d '{"question":"How do I rotate my API key?"}'
curl localhost:8080/result/{id}    # 200 running → 200 done (same result object)
```

The result object (one shape on every surface — CLI `--json`, MCP, eval):

```json
{
  "question": "…", "resolved": true, "answer": "…", "missing": null,
  "claims": [{ "claim": "…", "supported": true, "supporting_chunk_ids": [1] }],
  "citations": [{ "number": 1, "page_path": "docs/api-keys.md", "ordinal": 0, "text": "…", "score": 0.49 }],
  "calls": 2, "seconds": 4.1, "estimated_cost": "— (non-default model)"
}
```

Escalate is the safe default: any unsupported claim, zero claims, or malformed
verifier output → escalate, with the missing[] payload. A rejected draft is
never printed.