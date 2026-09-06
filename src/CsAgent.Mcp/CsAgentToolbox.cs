using CsAgent.Core;

namespace CsAgent.Mcp;

/// <summary>
/// The engine behind the MCP tools. Built from the process environment when the
/// server runs for real (the MCP host forwards CS_AGENT_* vars); built with an
/// injected pipeline factory in tests (the fake-or-real seam).
/// </summary>
public sealed class CsAgentToolbox(Func<AskPipeline> pipelineFactory, Func<string, IngestSummary>? ingest = null)
{
    public static CsAgentToolbox FromEnvironment() =>
        new(() => CsAgentRuntime.CreateAskPipeline(CsAgentConfig.FromCurrentEnvironment(
            new HashSet<string>(["fixtures"]))),
            ingest: path => CsAgentRuntime.IngestPath(CsAgentConfig.FromCurrentEnvironment(
                new HashSet<string>(["fixtures"])), path));

    public AskResult Ask(string question) => pipelineFactory().Run(question);

    public IngestSummary Ingest(string path) =>
        ingest?.Invoke(path)
        ?? throw new CsAgentException(new CsAgentError(
            "ingest", "not-configured", "ingest is not available in this configuration"));
}