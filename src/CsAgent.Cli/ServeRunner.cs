using System.Net;
using System.Net.Sockets;
using CsAgent.Core;
using CsAgent.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CsAgent.Cli;

/// <summary>
/// `cs-agent serve [--port N]` — Kestrel hosting the Http mapping. Startup
/// fail-fast (validate loudly applies to the server process, not per
/// request): port validation, then File.Exists on the store BEFORE
/// constructing it (construction would EnsureSchema+SetMeta a stray empty
/// .db on refusal), then HasDocuments.
/// </summary>
public static class ServeRunner
{
    /// <summary>
    /// Pure parse of CS_AGENT_BIND — a single IP literal. Null/whitespace defaults
    /// to loopback (host installs keep today's behavior). Hostnames are rejected:
    /// DNS-dependent startup is not validate-loudly. The wildcards 0.0.0.0/:: are
    /// allowed (the container case); IPv4-mapped IPv6 normalizes to plain IPv4.
    /// Returns the parsed IPAddress (mapped form already normalized) — one parse,
    /// loopcheck and IPv6 bracketing read off the object.
    /// </summary>
    public static IPAddress ParseBind(string? raw)
    {
        var bind = string.IsNullOrWhiteSpace(raw) ? "127.0.0.1" : raw;
        if (!IPAddress.TryParse(bind, out var ip))
            throw new CsAgentException(new CsAgentError(
                "serve", "bad-bind",
                $"CS_AGENT_BIND must be a single IP address (not a hostname, not a subnet); got \"{raw}\"."));
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    public static int Run(string[] serveArgs, CsAgentConfig cfg)
    {
        // port: --port flag wins, then CS_AGENT_PORT, then default (both cheap — Open Question resolved)
        string? rawPort = null;
        var source = "CS_AGENT_PORT";
        if (serveArgs is ["serve", "--port", var p])
        {
            rawPort = p;
            source = "--port";
        }
        else if (serveArgs.Length != 1)
        {
            Console.Error.WriteLine(new CsAgentError("serve", "bad-args", "usage: cs-agent serve [--port N]"));
            return 1;
        }
        rawPort ??= Environment.GetEnvironmentVariable("CS_AGENT_PORT");

        var port = 5123;
        if (rawPort is not null && (!int.TryParse(rawPort, out port) || port < 1 || port > 65535))
        {
            Console.Error.WriteLine(new CsAgentError("serve", "bad-port",
                $"Port must be an integer in [1, 65535]; got \"{rawPort}\" ({source})."));
            return 1;
        }

        // bind: CS_AGENT_BIND, default loopback (host installs unchanged); validated
        // BEFORE the store checks, same fail-fast shape as the port above. The
        // non-loopback warning prints HERE (before store checks) so it reaches
        // docker logs even when a store error follows.
        IPAddress bindIp;
        try
        {
            bindIp = ParseBind(Environment.GetEnvironmentVariable("CS_AGENT_BIND"));
        }
        catch (CsAgentException ex)
        {
            Console.Error.WriteLine(ex.Error);
            return 1;
        }
        if (!IPAddress.IsLoopback(bindIp))
            Console.Error.WriteLine($"warning: binding {bindIp} — the API is no-auth and unrated; expose only behind a trusted proxy or firewall (CS_AGENT_BIND).");

        var storePath = CorpusPointer.ResolveStorePath(cfg.StorePath);
        if (!File.Exists(storePath))
            throw new CsAgentException(new CsAgentError(
                "serve", "store-missing",
                $"No store at '{storePath}' — run `cs-agent ingest <path-or-url>` first, or set CS_AGENT_STORE."));
        using (var probe = new SqliteVectorStore(storePath, cfg.EmbeddingModel)) // constructor fails loudly on corrupt-store / embedding-model-mismatch
        {
            if (!probe.HasDocuments)
                throw new CsAgentException(new CsAgentError(
                    "serve", "empty-store",
                    $"Store '{storePath}' has no documents — re-ingest the corpus."));
        }

        var pipeline = CsAgentRuntime.CreateAskPipeline(cfg); // ONE singleton pipeline (eng-review D2)
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddOpenApi(); // /openapi/v1.json documents the surface
        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false); // server header suppressed
        var bind = bindIp.ToString();
        var brackets = bindIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind; // UseUrls requires [..] around IPv6 literals
        builder.WebHost.UseUrls($"http://{brackets}:{port}"); // loopback by default; CS_AGENT_BIND widens (container/proxy case)
        var app = builder.Build();
        app.MapOpenApi();
        app.MapCsAgentApi(pipeline);
        Console.WriteLine($"listening on http://{brackets}:{port} — POST /ask · GET /result/{{id}} · GET /health · /openapi/v1.json");
        try
        {
            app.Run(); // blocks; Ctrl+C = graceful shutdown, running jobs cancelled
        }
        catch (AddressInUseException)
        {
            Console.Error.WriteLine(new CsAgentError("serve", "port-in-use",
                $"Port {port} is already in use. Pass --port N or set CS_AGENT_PORT."));
            return 1;
        }
        return 0;
    }
}