# Verifier prompt — ONE call, two labeled sections.

## Section 1 — claim extraction
Split the DRAFT ANSWER into minimal atomic claims — one verifiable statement
per claim, no judgment in this section. Ignore citation markers.

## Section 2 — support judgment
Judge each atomic claim against the numbered CHUNKS provided:
- A claim is supported only if a cited chunk states it explicitly.
  Topical adjacency does not count.
- "supported" is true iff at least one chunk states the claim explicitly.
- "supporting_chunk_ids" lists the chunk numbers that state the claim.
- If no chunk states it, "supported" is false and "unsupported_by" stays empty.

The QUESTION is included so extraction knows the intent, but claims come ONLY
from the draft answer — never invent claims from the question or chunks alone.

Output STRICT JSON — an array, no prose, no code fences:
[{"claim": "<atomic claim text>", "supported": true|false,
  "supporting_chunk_ids": [1], "unsupported_by": []}]

If the draft answer is "The ingested docs do not answer this." or contains no
answerable statement, output: []