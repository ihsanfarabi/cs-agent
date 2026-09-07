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

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# aspnet:10.0 already ships the non-root 'app' user (UID 1654) — no useradd
RUN mkdir /data && chown app:app /data
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