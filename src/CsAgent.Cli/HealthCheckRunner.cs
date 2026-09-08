using CsAgent.Core;

namespace CsAgent.Cli;

/// <summary>
/// `cs-agent healthcheck [--port N]` — GET the local /health endpoint. The
/// in-image HEALTHCHECK probe (the chiseled base ships no shell/curl, so the
/// image probes through the CLI itself). Zero model calls, zero store access:
/// no CsAgentConfig is taken, and Program.cs dispatches this BEFORE config
/// construction so the probe works in containers without CS_AGENT_MODEL_KEY.
/// Success is silent (exit 0) so HEALTHCHECK logs stay clean; every failure is
/// a named structured error + exit 1.
/// </summary>
public static class HealthCheckRunner
{
    public static int Run(string[] args, TimeSpan? timeout = null)
    {
        // port: --port flag wins, then CS_AGENT_PORT, then default — same
        // precedence and validation as serve
        string? rawPort = null;
        var source = "CS_AGENT_PORT";
        if (args is ["healthcheck", "--port", var p])
        {
            rawPort = p;
            source = "--port";
        }
        else if (args.Length != 1)
        {
            Console.Error.WriteLine(new CsAgentError("healthcheck", "bad-args", "usage: cs-agent healthcheck [--port N]"));
            return 1;
        }
        rawPort ??= Environment.GetEnvironmentVariable("CS_AGENT_PORT");

        var port = 5123;
        if (rawPort is not null && (!int.TryParse(rawPort, out port) || port < 1 || port > 65535))
        {
            Console.Error.WriteLine(new CsAgentError("healthcheck", "bad-port",
                $"Port must be an integer in [1, 65535]; got \"{rawPort}\" ({source})."));
            return 1;
        }

        // The probe runs inside the container, so the target is always
        // loopback — 0.0.0.0 (the container default bind) includes it. Only a
        // CS_AGENT_BIND overridden to a specific non-loopback IP would dodge
        // the probe; documented in the README rather than parsed here.
        var url = $"http://127.0.0.1:{port}/health";
        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(3) };
        try
        {
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(new CsAgentError("healthcheck", "bad-status",
                    $"Health endpoint returned HTTP {(int)response.StatusCode}; expected 200."));
                return 1;
            }
            return 0;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(new CsAgentError("healthcheck", "unreachable",
                $"Health endpoint refused the connection at {url} — is 'cs-agent serve' running?"));
            return 1;
        }
        catch (TaskCanceledException) // HttpClient timeout
        {
            Console.Error.WriteLine(new CsAgentError("healthcheck", "timeout",
                $"Health endpoint did not respond within {(timeout ?? TimeSpan.FromSeconds(3)).TotalSeconds:0}s."));
            return 1;
        }
    }
}