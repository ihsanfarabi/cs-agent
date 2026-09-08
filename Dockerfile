# Multi-stage, framework-dependent, cross-compiled (design-2026-09-07-docker-ghcr.md Approach A,
# revised per eng review: SDK runs native, TARGETARCH picks the RID — no QEMU).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
# csproj layer first: restore hits the Docker layer cache on unchanged deps
COPY src/CsAgent.Core/*.csproj src/CsAgent.Core/
COPY src/CsAgent.Http/*.csproj src/CsAgent.Http/
COPY src/CsAgent.Cli/*.csproj src/CsAgent.Cli/
RUN dotnet restore -r linux-$TARGETARCH src/CsAgent.Cli/CsAgent.Cli.csproj
COPY src/ src/
# /p:AssemblyName is a global MSBuild property — it would rename the referenced
# Core/Http assemblies too (duplicate cs-agent.dll identity breaks the CLI build),
# so publish under natural names and rename only the apphost executable.
RUN dotnet publish src/CsAgent.Cli/CsAgent.Cli.csproj -c Release --no-restore \
    -r linux-$TARGETARCH --self-contained false \
    -o /app && mv /app/CsAgent.Cli /app/cs-agent
# chiseled runtime has no shell, so /data cannot be RUN-created there; build it
# here and COPY it with --chown below. .keep guards against builders that skip
# empty directories — a harmless hidden file lands in the volume.
RUN mkdir -p /data && touch /data/.keep

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
# chiseled: no shell, no package manager; same non-root 'app' user (UID/GID
# 1654) as the standard base, non-root by default. --chown replicates the old
# RUN chown so a FIRST named-volume mount still copies app-owned ownership
# into the volume (without it, a fresh named volume is root-owned and the
# app user cannot write the store).
COPY --from=build --chown=1654:1654 /data /data
WORKDIR /app
COPY --from=build /app .
USER app
# container defaults: bind all interfaces (loopback is unreachable through -p);
# store lives on the /data volume so ingest and serve share it across runs
ENV CS_AGENT_BIND=0.0.0.0 \
    CS_AGENT_STORE=/data/cs-agent.db
VOLUME /data
EXPOSE 5123
# exec form (no shell in chiseled); the probe GETs 127.0.0.1:PORT/health with
# the same port precedence as serve; its 3s in-process timeout fits the 5s
# docker probe timeout
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/app/cs-agent", "healthcheck"]
ENTRYPOINT ["/app/cs-agent"]