# Design: Dockerfile + GHCR image for cs-agent

Date: 2026-09-07. Queue position: design-2026-09-05.md post-publish order item 3
(URL crawl ✅ → HTTP API ✅ → **Docker + GHCR** → Postgres+pgvector).

## Problem Statement

The HTTP API (`cs-agent serve`) exists but ships only as a .NET tool. The
post-publish queue names a container image: `ghcr.io/ihsanfarabi/cs-agent`,
pulled and run in one line. Two things block it:

1. `ServeRunner` hard-binds `http://127.0.0.1:{port}` (ServeRunner.cs:61) —
   inside a container, a published port cannot reach a loopback bind, so
   `docker run -p 5123:5123` would connect to nothing.
2. No Dockerfile, no image publish workflow.

## What Makes This Cool

The engine's whole demo collapses to two `docker run` lines with no .NET
install, no tool path, no store plumbing — ingest into a named volume, then
serve from it. That is the strongest showcase form of the project's "verdict
before output" HTTP surface.

## Constraints

- Env-vars-only configuration (CLAUDE.md rule): the bind must be an env var,
  not a config file or image-baked CLI flag.
- Validate loudly: a bad bind value is a named structured error, exit 1,
  before any socket exists — same family as `bad-port`.
- Escalate-is-safe / one-result-object / verifier invariants: untouched. No
  Core pipeline code changes at all in this increment.
- `publish.yml` already guards tag == csproj `Version`; image tags derive
  from the same git tag, so image version == NuGet version with no second
  guard.
- No auto-commits; commit only when the user asks.

## Premises (all agreed in session, 2026-09-07)

1. **Bind via `CS_AGENT_BIND` env var** (no CLI flag — container never needs
   one, two parse paths would be tested dead weight). Default `127.0.0.1`:
   host-installed users keep today's behavior. The Dockerfile bakes
   `ENV CS_AGENT_BIND=0.0.0.0` so `-p` publishing works.
2. **Validation**: `IPAddress.TryParse` — a single IP literal only. Hostnames
   rejected (DNS-dependent startup is not validate-loudly). Wildcard
   `0.0.0.0` / `::` ALLOWED (that is the container case); mapped IPv6
   (`::ffff:127.0.0.1`) normalized to IPv4. IPv6 literals emitted with
   `[...]` brackets for `UseUrls`.
3. **Non-loopback bind prints a one-line stderr warning** before the
   listening line: the API is no-auth, expose only behind a trusted
   proxy/firewall. It is a warning, not a refusal — the auth work is a later
   queue item and reverse-proxy deployment is the documented container story.
4. **Full CLI in the image** (not a serve-only image): ENTRYPOINT is the CLI
   binary, every subcommand works in the container. `ENV
   CS_AGENT_STORE=/data/cs-agent.db` + `VOLUME /data` (owned by a non-root
   `app` user). README demo: `docker run … ingest`, then `docker run …
   serve`, sharing a named volume.
5. **Publish workflow**: new `.github/workflows/docker.yml`. `pull_request`:
   build only, never push (Dockerfile rot caught in CI, ~2-3 min). Tag
   `v*`: build + push `ghcr.io/ihsanfarabi/cs-agent` with the tag, the
   un-prefixed version, and `latest`. Separate file from `publish.yml` so
   `packages:write` never mixes into the NuGet OIDC job's permission
   surface.
6. **Multi-arch, native runners**: matrix `ubuntu-latest` (amd64) +
   `ubuntu-24.04-arm` (arm64) — free on public repos, no QEMU, and the
   maintainer's Apple Silicon pulls a native image.

## Approaches Considered

### Approach A: multi-stage, framework-dependent (chosen)

`sdk:10.0` stage restores + publishes the CLI; runtime stage is
`mcr.microsoft.com/dotnet/aspnet:10.0` (Kestrel needs the ASP.NET runtime)
with a non-root user, `/data` volume, `ENTRYPOINT ["/app/cs-agent"]`
(published with `/p:AssemblyName=cs-agent` so the binary name matches the
tool name).

- ~120MB image; runtime patches come free with base re-pull; matches CI's
  `dotnet-version: 10.0.x`.
- csproj files copied before `COPY . .` so restore hits the Docker layer
  cache on unchanged dependencies.

### Approach B: self-contained onto runtime-deps

`PublishSelfContained` + trim onto `runtime-deps:10.0`. No runtime in image,
but ASP.NET self-containment is a known trimming sharp edge and every SDK
bump re-rolls it. Equal-or-worse size for one exe. Declined.

### Approach C: SDK image + `dotnet tool install` from nuget.org

~800MB, couples image build to published package availability. Worst of
both. Declined.

## Design detail

### ServeRunner changes (`src/CsAgent.Cli/ServeRunner.cs`)

Extract a pure helper (the port parsing stays inline, matching today):

```csharp
/// Pure parse of CS_AGENT_BIND — a single IP literal, hostnames rejected,
/// wildcard allowed (container case), mapped IPv6 normalized to IPv4.
internal static string ParseBind(string? raw)
{
    var bind = raw is null || string.IsNullOrWhiteSpace(raw) ? "127.0.0.1" : raw;
    if (!IPAddress.TryParse(bind, out var ip))
        throw new CsAgentException(new CsAgentError("serve", "bad-bind",
            $"CS_AGENT_BIND must be a single IP address (not a hostname); got \"{raw}\"."));
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
    return ip.ToString();
}
```

`Run` calls it with `Environment.GetEnvironmentVariable("CS_AGENT_BIND")`,
then:

```csharp
var bind = ParseBind(Environment.GetEnvironmentVariable("CS_AGENT_BIND"));
var brackets = bind.Contains(':') ? $"[{bind}]" : bind;   // IPv6 literal needs [..] for UseUrls
builder.WebHost.UseUrls($"http://{brackets}:{port}");
if (!IPAddress.IsLoopback(IPAddress.Parse(bind)))
    Console.Error.WriteLine($"warning: binding {bind} — the API is no-auth; expose only behind a trusted proxy or firewall (CS_AGENT_BIND).");
```

(The listening-line message keeps its current shape with the resolved
address.) Error path matches `bad-port`: named error, exit 1, no socket, no
stray artifacts.

### Dockerfile (repo root) + `.dockerignore`

`.dockerignore` (new file, repo root) keeps host artifacts out of the
build context — without it `COPY src/ src/` drags local `bin/`, `obj/`,
and any stray `.db` files into the image:

```
**/bin/
**/obj/
*.db
eval-results/
.git/
.github/
docs/
prototype/
tests/
```

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
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
ENV CS_AGENT_BIND=0.0.0.0 CS_AGENT_STORE=/data/cs-agent.db
VOLUME /data
EXPOSE 5123
ENTRYPOINT ["/app/cs-agent"]
```

- Only the CLI's project graph is restored and published (Core + Http).
  `CsAgent.Mcp` source rides along in the copy layer but is never built —
  MCP ships as a NuGet tool, not in the image; documented in README.
- Default container command is `cs-agent` with no args → today prints usage
  exit 1; README examples always pass `serve` (or `ingest`).
- No HEALTHCHECK directive in v1: the aspnet image ships no curl/wget.
  `GET /health` exists; orchestrators and `docker inspect`-style checks
  call it (README documents the line).

### `.github/workflows/docker.yml`

As presented and approved in session (PR build-only matrix + tag push with
`docker/metadata-action@v5` tags `vX.Y.Z`, `X.Y.Z`, `latest`;
`build-push-action@v6` with `cache-from/to: type=gha`; login only on tag
refs). PR tags are `pr-N` labels but never pushed.

### README

- New "Docker" section after the HTTP section: pull line, ingest-into-volume
  + serve examples, `-e CS_AGENT_MODEL_KEY`, `-p 5123:5123`.
- Limitation #2 rewritten: loopback by default; `CS_AGENT_BIND` exists for
  container/reverse-proxy use and prints the no-auth warning; the image
  bakes `0.0.0.0` — put it behind a proxy.
- Limitation #5 (in-process job memory) unchanged — container restart
  semantics already documented ("restart loses ids, resubmit").

## Testing

- **Unit (xUnit, `ServeRunnerTests.cs`)**: `ParseBind` — null/empty →
  `127.0.0.1`; `192.168.1.5` accepted; `::1` accepted; `0.0.0.0` accepted;
  `::ffff:127.0.0.1` → `127.0.0.1`; `localhost` rejected (`bad-bind`);
  `not-an-ip` rejected. Plus one `Run` fail-fast: env `CS_AGENT_BIND=bad`
  → exit 1 (env mutated and restored in try/finally, matching the existing
  fail-fast idiom).
- **Local smoke (before any tag)**: `docker build .`, then ingest
  `fixtures/` into a named volume and curl `/health` + one `/ask` through
  `-p`. Verifies bind, volume ownership (non-root), store path, and that
  no stray `.db` lands anywhere but `/data`.
- **CI**: PR shows both platform builds green; first tag push shows the
  multi-arch manifest on ghcr.io and a pullable `latest`.
- No Core changes → eval gates and the 119-test suite are untouched except
  for the new ServeRunner tests.

## Success Criteria

- `docker run -d -p 5123:5123 -v cs-agent-data:/data -e CS_AGENT_MODEL_KEY=…
  ghcr.io/ihsanfarabi/cs-agent serve` answers `/health` and `/ask` from a
  volume-ingested store.
- Host install (`dotnet tool`) behavior unchanged: still binds loopback
  unless `CS_AGENT_BIND` says otherwise, warning on non-loopback.
- CI proves the Dockerfile on every PR; tag push publishes a multi-arch
  image whose version matches the NuGet package from the same tag.
- README carries the Docker demo and the honest limitation text.

## Not in scope (v1 of this item)

- Auth/rate limiting (later queue item — the warning line is the bridge).
- Postgres+pgvector store (next queue item).
- MCP server in the image (tool-only surface, documented).
- HEALTHCHECK directive, distroless/chiseled base, image signing
  (cosign/attestations) — candidates for a later hardening pass.
- CI container-run test (the local smoke covers it; CI does build-only).

## Next Steps

Implementation plan: `docs/superpowers/plans/2026-09-07-docker-ghcr.md`
(written next via superpowers:writing-plans), then execution, then
`/gstack-plan-eng-review` on the plan, then ship on a `v0.3.0` tag.