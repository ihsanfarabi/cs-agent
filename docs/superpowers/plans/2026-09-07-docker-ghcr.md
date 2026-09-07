# Docker + GHCR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `ghcr.io/ihsanfarabi/cs-agent` as a multi-arch container image running the full CLI (ingest + serve) against a `/data` volume, plus the `CS_AGENT_BIND` env var that makes `serve` bindable outside loopback.

**Architecture:** One CLI change (bind parse + non-loopback warning in `ServeRunner`, zero Core changes), one multi-stage Dockerfile (framework-dependent publish of `CsAgent.Cli` onto the aspnet:10.0 runtime, non-root user), one new workflow (`docker.yml`: PR build-only amd64, tag push amd64+arm64 via QEMU buildx), README Docker section.

**Tech Stack:** .NET 10 (SDK/aspnet base images), Docker buildx + QEMU, GitHub Actions (docker/build-push-action@v6, metadata-action@v5, login-action@v3), xUnit.

**Spec:** `docs/designs/design-2026-09-07-docker-ghcr.md` — the plan argues from the spec; read both.

## Global Constraints

- Env-vars-only configuration; the bind is `CS_AGENT_BIND`, default `127.0.0.1`, no CLI flag.
- Validate loudly: bad bind value = named structured error `serve/bad-bind`, exit 1, before any socket exists and before any store construction.
- Zero changes to `src/CsAgent.Core/` — pipeline, store, eval untouched; the 119-test suite must stay green with only new tests added.
- Image name `ghcr.io/ihsanfarabi/cs-agent`; base images `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0`; publish with `/p:AssemblyName=cs-agent`.
- **No Co-Authored-By or any attribution trailer in commits** (workspace rule).
- Commit messages: Conventional Commits, subject ≤50 chars, imperative, no period.
- `dotnet test` runs from repo root (`dotnet test -c Release`); RTK not available to executor subagents unless inherited — plain `dotnet` is fine.

---

### Task 1: `ParseBind` pure helper (TDD)

**Files:**
- Modify: `src/CsAgent.Cli/ServeRunner.cs` (add helper inside `ServeRunner`, above `Run`)
- Test: `tests/CsAgent.Tests/ServeRunnerTests.cs` (append new region at end of class)

**Interfaces:**
- Consumes: `CsAgentException`, `CsAgentError` (existing, `CsAgent.Core`).
- Produces: `public static string ServeRunner.ParseBind(string? raw)` — null/whitespace → `"127.0.0.1"`; single IP literal accepted (IPv4, IPv6 incl. wildcard `::`/`0.0.0.0`); IPv4-mapped IPv6 normalized to plain IPv4; anything else (hostnames, garbage, subnet notation) throws `CsAgentException` with `Error.Component == "serve"`, `Error.Code == "bad-bind"`. Task 2 wires it into `Run`.

- [ ] **Step 1: Write the failing tests**

Append to `ServeRunnerTests` (after the existing `EmbeddingModelMismatch_Throws` fact):

```csharp
    // --- CS_AGENT_BIND (ParseBind, pure — Docker/queue item 3) ---

    [Fact]
    public void ParseBind_NullOrWhitespace_DefaultsLoopback()
    {
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind(null));
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind(""));
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind("   "));
    }

    [Fact]
    public void ParseBind_IpLiterals_Accepted()
    {
        Assert.Equal("192.168.1.5", ServeRunner.ParseBind("192.168.1.5"));
        Assert.Equal("::1", ServeRunner.ParseBind("::1"));
        Assert.Equal("0.0.0.0", ServeRunner.ParseBind("0.0.0.0")); // container case
        Assert.Equal("::", ServeRunner.ParseBind("::"));           // container case
    }

    [Fact]
    public void ParseBind_Ipv4Mapped_NormalizesToIpv4()
    {
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind("::ffff:127.0.0.1"));
    }

    [Theory]
    [InlineData("localhost")]   // hostname: DNS-dependent startup is not validate-loudly
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.256")]  // out-of-range octet
    [InlineData("127.0.0.1/32")] // CIDR, not a host address
    [InlineData("http://127.0.0.1")]
    public void ParseBind_Rejects_ThrowsBadBind(string raw)
    {
        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.ParseBind(raw));
        Assert.Equal("serve", ex.Error.Component);
        Assert.Equal("bad-bind", ex.Error.Code);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~ParseBind"`
Expected: compile error — `ServeRunner.ParseBind` does not exist.

- [ ] **Step 3: Write the helper**

In `src/CsAgent.Cli/ServeRunner.cs`, add `using System.Net;` at the top and this method inside `public static class ServeRunner`, above `Run`:

```csharp
    /// <summary>
    /// Pure parse of CS_AGENT_BIND — a single IP literal. Null/whitespace defaults
    /// to loopback (host installs keep today's behavior). Hostnames are rejected:
    /// DNS-dependent startup is not validate-loudly. The wildcards 0.0.0.0/:: are
    /// allowed (the container case); IPv4-mapped IPv6 normalizes to plain IPv4.
    /// </summary>
    public static string ParseBind(string? raw)
    {
        var bind = string.IsNullOrWhiteSpace(raw) ? "127.0.0.1" : raw;
        if (!IPAddress.TryParse(bind, out var ip))
            throw new CsAgentException(new CsAgentError(
                "serve", "bad-bind",
                $"CS_AGENT_BIND must be a single IP address (not a hostname, not a subnet); got \"{raw}\"."));
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
```

Note: `IPAddress.TryParse` accepts `"::"` and `"0.0.0.0"` (wildcards) and rejects every `[InlineData]` case above — verify with the test run, not by trust.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~ParseBind"`
Expected: PASS (all 8 cases — 3 facts + the 5-case theory).

- [ ] **Step 5: Run the full suite (nothing else broke)**

Run: `dotnet test -c Release`
Expected: 119 existing + 8 new = 127 passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/CsAgent.Cli/ServeRunner.cs tests/CsAgent.Tests/ServeRunnerTests.cs
git commit -m "feat: ParseBind helper for CS_AGENT_BIND"
```

---

### Task 2: Wire bind into `ServeRunner.Run` (TDD)

**Files:**
- Modify: `src/CsAgent.Cli/ServeRunner.cs:19-77` (Run body: parse bind after port, before store checks; use it in `UseUrls`; warning line)
- Test: `tests/CsAgent.Tests/ServeRunnerTests.cs` (one new fail-fast test)

**Interfaces:**
- Consumes: `ServeRunner.ParseBind(string? raw)` from Task 1 (exact signature above).
- Produces: `serve` behavior — env `CS_AGENT_BIND` honored; `http://{bind}:{port}` URL (IPv6 bracketed); non-loopback bind prints one stderr warning; bad value → error printed, return 1. Task 3's Dockerfile relies on `CS_AGENT_BIND=0.0.0.0` actually binding all interfaces.

- [ ] **Step 1: Write the failing test**

Append to `ServeRunnerTests`:

```csharp
    [Fact]
    public void BadBindEnv_Returns1_NoStoreTouched()
    {
        // bind is validated BEFORE the store checks (bad-port idiom): this config
        // points at a missing store, so reaching store-missing would prove wrong order
        var prev = Environment.GetEnvironmentVariable("CS_AGENT_BIND");
        Environment.SetEnvironmentVariable("CS_AGENT_BIND", "localhost");
        try
        {
            Assert.Equal(1, ServeRunner.Run(["serve"], Config()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CS_AGENT_BIND", prev);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~BadBindEnv"`
Expected: FAIL — throws `CsAgentException` (`store-missing`) instead of returning 1, because `Run` does not read `CS_AGENT_BIND` yet. (If it fails with a different error, stop and re-read the current `Run` before editing.)

- [ ] **Step 3: Wire the bind into Run**

In `ServeRunner.Run`, after the port validation block (`rawPort ??= …` through the `port` default/if) and BEFORE `var storePath = CorpusPointer.ResolveStorePath(cfg.StorePath);`, insert:

```csharp
        // bind: CS_AGENT_BIND, default loopback (host installs unchanged); validated
        // BEFORE the store checks, same fail-fast shape as the port above
        string bind;
        try
        {
            bind = ParseBind(Environment.GetEnvironmentVariable("CS_AGENT_BIND"));
        }
        catch (CsAgentException ex)
        {
            Console.Error.WriteLine(ex.Error);
            return 1;
        }
```

Then replace the two lines `builder.WebHost.UseUrls($"http://127.0.0.1:{port}"); // loopback-only bind — explicitly no-auth API` and the listening-line `Console.WriteLine($"listening on http://127.0.0.1:{port} — …")` with:

```csharp
        var brackets = bind.Contains(':') ? $"[{bind}]" : bind; // UseUrls requires [..] around IPv6 literals
        if (!IPAddress.IsLoopback(IPAddress.Parse(bind)))
            Console.Error.WriteLine($"warning: binding {bind} — the API is no-auth and unrated; expose only behind a trusted proxy or firewall (CS_AGENT_BIND).");
        builder.WebHost.UseUrls($"http://{brackets}:{port}"); // loopback by default; CS_AGENT_BIND widens (container/proxy case)
```

and

```csharp
        Console.WriteLine($"listening on http://{brackets}:{port} — POST /ask · GET /result/{{id}} · GET /health · /openapi/v1.json");
```

Keep everything else in `Run` byte-identical (store fail-fast, pipeline creation, Kestrel options, `AddressInUseException` catch).

- [ ] **Step 4: Run the new test, then the full suite**

Run: `dotnet test tests/CsAgent.Tests -c Release --filter "FullyQualifiedName~BadBindEnv"`
Expected: PASS (returns 1, no exception).

Run: `dotnet test -c Release`
Expected: 128 passing, 0 failing (127 from Task 1 + this one).

- [ ] **Step 5: Manual sanity (optional, 1 min, no Docker yet)**

```bash
CS_AGENT_MODEL_KEY=x CS_AGENT_BIND=0.0.0.0 dotnet run --project src/CsAgent.Cli -- serve 2>&1 | head -3
```
Expected output line 1-2: the `warning: binding 0.0.0.0` stderr line, then either the listening line or a named store error (no store configured). Confirms warning + URL shape live. Not a gate — the suite is.

- [ ] **Step 6: Commit**

```bash
git add src/CsAgent.Cli/ServeRunner.cs tests/CsAgent.Tests/ServeRunnerTests.cs
git commit -m "feat: serve honors CS_AGENT_BIND with non-loopback warning"
```

---

### Task 3: `.dockerignore` + Dockerfile + local build

**Files:**
- Create: `.dockerignore` (repo root)
- Create: `Dockerfile` (repo root)

**Interfaces:**
- Consumes: the CLI project graph `src/CsAgent.Cli` → `CsAgent.Core` + `CsAgent.Http` (nothing else is restored or published).
- Produces: a local image `cs-agent:dev` building cleanly from `docker build .` — Task 4's workflow builds this exact Dockerfile; Task 6 runs it end-to-end.

- [ ] **Step 1: Write `.dockerignore`**

```
**/bin/
**/obj/
*.db
.cs-agent-recency
eval-results/
.git/
.github/
docs/
prototype/
tests/
```

(Rationale: `COPY src/ src/` in the Dockerfile would otherwise drag host `bin/`, `obj/`, and stray `.db` files into the build context; `tests/`/`docs/` etc. are not in the publish graph.)

- [ ] **Step 2: Write `Dockerfile`**

```dockerfile
# Multi-stage, framework-dependent (design-2026-09-07-docker-ghcr.md, Approach A).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# csproj layer first: restore hits the Docker layer cache on unchanged deps
COPY src/CsAgent.Core/*.csproj src/CsAgent.Core/
COPY src/CsAgent.Http/*.csproj src/CsAgent.Http/
COPY src/CsAgent.Cli/*.csproj src/CsAgent.Cli/
RUN dotnet restore src/CsAgent.Cli/CsAgent.Cli.csproj
COPY src/ src/
RUN dotnet publish src/CsAgent.Cli/CsAgent.Cli.csproj -c Release --no-restore \
    -o /app /p:AssemblyName=cs-agent

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN useradd --system --create-home app && mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=build /app .
USER app
# container defaults: bind all interfaces (loopback is unreachable through -p);
# store lives on the /data volume so ingest and serve share it across runs
ENV CS_AGENT_BIND=0.0.0.0 \
    CS_AGENT_STORE=/data/cs-agent.db
VOLUME /data
EXPOSE 5123
ENTRYPOINT ["/app/cs-agent"]
```

- [ ] **Step 3: Verify Docker is up, then build**

Run: `docker info > /dev/null && echo up` (if this fails, Docker Desktop is not running — start it, or report BLOCKED and let the user decide; do not silently skip).
Run: `docker build -t cs-agent:dev .`
Expected: build completes; final stage ~120MB (`docker images cs-agent:dev`).

- [ ] **Step 4: Smoke the binary name and default command**

Run: `docker run --rm cs-agent:dev 2>&1 | head -2`
Expected: the CLI usage line (`usage: cs-agent <ingest … | ask … | …>`), exit code 1 — proves ENTRYPOINT resolves the published `cs-agent` binary and the runtime works. No store warnings expected yet (no subcommand ran).

- [ ] **Step 5: Commit**

```bash
git add .dockerignore Dockerfile
git commit -m "feat: Dockerfile — full CLI on aspnet:10.0, /data volume, non-root"
```

---

### Task 4: `.github/workflows/docker.yml`

**Files:**
- Create: `.github/workflows/docker.yml`

**Interfaces:**
- Consumes: `Dockerfile` from Task 3 (same build context, same file).
- Produces: on every PR — an amd64 build of the Dockerfile (never pushed); on tag `v*` — `ghcr.io/ihsanfarabi/cs-agent` pushed with tags `vX.Y.Z`, `X.Y.Z`, `latest` as ONE multi-arch manifest (amd64+arm64, QEMU). Version consistency rides on `publish.yml`'s existing tag==csproj guard on the same tag push; no second guard here.

- [ ] **Step 1: Write the workflow**

```yaml
name: docker

on:
  pull_request:
  push:
    tags: ["v*"]

permissions:
  packages: write

jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 40 # QEMU arm64 publish is slow (~10-20 min); PR builds finish in ~3
    steps:
      - uses: actions/checkout@v4

      - name: Set up QEMU (arm64 emulation for the tag build)
        uses: docker/setup-qemu-action@v3

      - uses: docker/setup-buildx-action@v3

      - name: GHCR login (tag pushes only — PRs never authenticate)
        uses: docker/login-action@v3
        if: startsWith(github.ref, 'refs/tags/')
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Image metadata (vX.Y.Z, X.Y.Z, latest on tag; pr-N on PR, unused)
        id: meta
        uses: docker/metadata-action@v5
        with:
          images: ghcr.io/ihsanfarabi/cs-agent

      - name: Build (push only on tag)
        uses: docker/build-push-action@v6
        with:
          context: .
          # PR: amd64 only (fast Dockerfile-rot check). Tag: both platforms,
          # one buildx invocation -> one multi-arch manifest.
          platforms: ${{ startsWith(github.ref, 'refs/tags/') && 'linux/amd64,linux/arm64' || 'linux/amd64' }}
          push: ${{ startsWith(github.ref, 'refs/tags/') }}
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

- [ ] **Step 2: YAML sanity check (local, no push)**

Run: `docker run --rm -v "$PWD/.github/workflows/docker.yml":/wf.yml:ro python:3-alpine python -c "import yaml,sys; yaml.safe_load(open('/wf.yml')); print('yaml ok')"`
Expected: `yaml ok`. (If the image pull is unavailable, `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/docker.yml')); print('yaml ok')"` locally is equivalent.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/docker.yml
git commit -m "ci: docker.yml — PR build check + multi-arch GHCR push on tags"
```

The workflow itself is verified live in Task 6 (this plan lands via PR, so the PR build IS its first real run).

---

### Task 5: README — Docker section + limitation rewrite

**Files:**
- Modify: `README.md` — new `## Docker` section after the `## HTTP API` section (after the `serve fails fast…` paragraph at ~line 236, before `## MCP server`); rewrite the loopback bullet in the HTTP API section (~line 229); the deferred-work line (~line 283).

**Interfaces:**
- Consumes: the container contract from Tasks 3-4 (image name, `/data`, `CS_AGENT_BIND`, `-p`).
- Produces: user-facing docs; Task 6 verifies the documented commands actually run.

- [ ] **Step 1: Rewrite the loopback bullet**

In the HTTP API section, replace:

```markdown
- The server binds `127.0.0.1` only — it is explicitly a no-auth,
  no-rate-limit local tool; non-loopback bind waits for auth work.
```

with:

```markdown
- The server binds `127.0.0.1` by default — it is explicitly a no-auth,
  no-rate-limit local tool. `CS_AGENT_BIND` (a single IP address, no
  hostnames) widens the bind for the container/reverse-proxy case and
  prints a warning; auth and rate limiting wait for later work.
```

- [ ] **Step 2: Add the Docker section**

Insert before `## MCP server`:

```markdown
## Docker

The same engine ships as a container image — no .NET install, no tool path.
The image is the full CLI: every subcommand works, and the store lives on a
`/data` volume so ingest and serve share it across runs.

```bash
# 1. ingest (into a named volume; local docs must be mounted, URLs crawl from the container)
docker run --rm -v cs-agent-data:/data -v "$PWD/docs:/docs:ro" \
    -e CS_AGENT_MODEL_KEY=sk-or-... \
    ghcr.io/ihsanfarabi/cs-agent ingest /docs

# 2. serve — image bakes CS_AGENT_BIND=0.0.0.0, so -p works
docker run -d --name cs-agent -p 5123:5123 -v cs-agent-data:/data \
    -e CS_AGENT_MODEL_KEY=sk-or-... \
    ghcr.io/ihsanfarabi/cs-agent serve
curl -s http://localhost:5123/health

# 3. ask (same poll flow as above; add ?evidence=true to audit escalations)
curl -s http://localhost:5123/ask -H 'Content-Type: application/json' \
    -d '{"question": "How do I rotate the API key?"}'
```

The image binds `0.0.0.0` (container default) — it is still a no-auth,
no-rate-limit API: publish the port to localhost only
(`-p 127.0.0.1:5123:5123`) or put it behind a trusted proxy. There is no
HEALTHCHECK directive in the image; orchestrators should probe
`GET /health`. Job memory, restart semantics, and all other HTTP limitations
above apply unchanged.
```

(Nested code fences: the outer block above is illustrative — in the actual README the two shell blocks are ordinary fenced blocks, not nested.)

- [ ] **Step 3: Update the deferred-work line**

Find the limitations line naming Docker as deferred (near `Persistence waits for the Docker/postgres work.` / `Deferred work: Docker packaging, Postgres storage, and …`) and drop `Docker packaging` from the deferred list; keep Postgres and the rest verbatim. The in-process-jobs limitation stays exactly as is — container restarts lose job ids, same as host restarts.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: Docker section, CS_AGENT_BIND in README"
```

---

### Task 6: Local end-to-end container verification + TODOS DONE entry

**Files:**
- Modify: `docs/designs/TODOS.md` (new DONE section at top)
- No code changes — this task verifies Tasks 1-5 as a whole.

**Interfaces:**
- Consumes: local image `cs-agent:dev` (built in Task 3; rebuild if Task 2/3 changed since), the fixtures corpus at `fixtures/`, a real `CS_AGENT_MODEL_KEY` in the environment.
- Produces: verified Docker story (ingest → serve → /health → /ask through published port, warning in logs) and the TODOS record.

**Prerequisite:** `CS_AGENT_MODEL_KEY` must be set in the shell (real key — ingest embeds, one ask spends 2 model calls, pennies). If unset, report BLOCKED and ask the user; do not fabricate a pass.

- [ ] **Step 1: Rebuild the image with all changes**

Run: `docker build -t cs-agent:dev .`
Expected: clean build.

- [ ] **Step 2: Ingest fixtures into a fresh volume**

```bash
docker volume rm cs-agent-smoke 2>/dev/null; docker volume create cs-agent-smoke
docker run --rm -v cs-agent-smoke:/data -v "$PWD/fixtures:/docs:ro" \
    -e CS_AGENT_MODEL_KEY="$CS_AGENT_MODEL_KEY" \
    cs-agent:dev ingest /docs
```
Expected: ingest output naming the store at `/data/cs-agent.db`, zero structured errors. (Fixture embeddings need the default embedding model `text-embedding-3-small` — leave `CS_AGENT_EMBEDDING_MODEL` unset.)

- [ ] **Step 3: Serve on a published port**

```bash
docker run -d --name cs-agent-smoke -p 5123:5123 -v cs-agent-smoke:/data \
    -e CS_AGENT_MODEL_KEY="$CS_AGENT_MODEL_KEY" \
    cs-agent:dev serve
```
Then: `docker logs cs-agent-smoke 2>&1 | head -3`
Expected: the `warning: binding 0.0.0.0` stderr line (proves the Task 2 warning fires in the container) followed by `listening on http://0.0.0.0:5123 — POST /ask · …`.

- [ ] **Step 4: Health + one real ask through the published port**

```bash
curl -s http://localhost:5123/health
curl -s http://localhost:5123/ask -H 'Content-Type: application/json' \
    -d '{"question": "What is the AskEvidence trace?"}' \
    | tee /dev/tty | grep -q '"id"'
```
Expected: `/health` → 200; `/ask` → `202` JSON with an `id`. Then poll `curl -s http://localhost:5123/result/<id>` until the status leaves `running` — a resolved answer OR a clean escalation (`resolved:false`, `missing[]`) both count as PASS; any 5xx is FAIL.

- [ ] **Step 5: Clean up**

```bash
docker rm -f cs-agent-smoke && docker volume rm cs-agent-smoke
```

- [ ] **Step 6: Write the TODOS DONE entry**

Prepend to the OPEN section of `docs/designs/TODOS.md` (above the current OPEN issue #6 entry, following the existing DONE-section format):

```markdown
## DONE: Dockerfile + GHCR image (closed 2026-09-07; was design-doc post-publish item 3)

**What shipped:** multi-stage Dockerfile (framework-dependent CLI publish onto
aspnet:10.0, non-root `app` user, `/data` volume, `CS_AGENT_BIND=0.0.0.0` +
`CS_AGENT_STORE=/data/cs-agent.db` baked in) + `.github/workflows/docker.yml`
(PR: amd64 build-only, never pushes; tag v*: one QEMU buildx build pushing
`ghcr.io/ihsanfarabi/cs-agent` as a single multi-arch manifest with `vX.Y.Z`,
`X.Y.Z`, `latest`). Core change: one — `ServeRunner` gained `ParseBind`
(`CS_AGENT_BIND`, default loopback, hostnames rejected, wildcard allowed,
IPv6 bracketed for UseUrls) plus the non-loopback stderr warning; bad value
is named error `serve/bad-bind`, exit 1, before any socket or store open.
Zero Core pipeline changes; eval gates untouched. Tests: 8 ParseBind cases +
1 Run fail-fast added (128 total, all green). The image is the full CLI —
ingest and serve share the `/data` volume; MCP stays a NuGet tool (not in the
image, documented). Workflow note: the originally sketched native-runner
matrix was replaced by a single QEMU job at plan time — per-platform matrix
pushes overwrite the tag manifest instead of merging; design doc premise 6
records the revision.
```

- [ ] **Step 7: Commit**

```bash
git add docs/designs/TODOS.md
git commit -m "docs: TODOS DONE entry for Docker + GHCR"
```

- [ ] **Step 8: Report**

Report per verification-before-completion: what ran, what passed, the one
live-model ask outcome. Tagging `v0.3.0` (which triggers the real GHCR push
and the NuGet publish) is the user's call, not this plan's — do not tag.

---

## Plan Self-Review (done at write time)

- Spec coverage: premises 1-6 → Tasks 1-4; README → Task 5; testing section
  (unit, local smoke, CI) → Tasks 1-2, 6, 4; success criteria → Task 6 +
  live tag run. Not-in-scope items have no tasks, correctly.
- Multi-arch correctness: single buildx invocation with both platforms is
  what produces one manifest — no digest-merge needed; PR/amd64-only keeps
  PR time ~3 min.
- Ordering: bind parse sits before store checks in `Run` (Task 2 test
  enforces this by pointing at a missing store).
- Deviation from design doc premise 6 (native matrix → QEMU) was recorded
  in the design doc before this plan was written.